# Read-gate priority: the active repo goes first

> `GitReadGate` bounds background git reads at three concurrent, which is the right cap for a
> spindle. What it does not do is decide *which* three. It is a single FIFO `SemaphoreSlim`, so a
> read for the repo the user is staring at is admitted in the order it happened to arrive — behind
> however many whole-tree sweeps for repos they are not looking at got there first.
>
> **Status: landed.** This is the third item of a three-part fix for "fetch completes but
> the ahead/behind number doesn't move for a minute". The first two shipped together:
>
> - **A — a fetch no longer answers with a working-tree walk.** `RefsChangedMessage` now takes
>   `IGitStatusReader.GetSyncSummary`, a refs-only read (`symbolic-ref` + `for-each-ref
>   %(upstream:track)`), instead of `GetStatusSummary`'s full `git status`. Landed.
> - **C — refreshes coalesce per repo.** `RepoStatusStore` keeps at most one read in flight per repo
>   with at most one re-run queued behind it, so clicking fetch three times costs one extra read
>   rather than three — each of which used to bump the repo's epoch and *discard* the answer the
>   previous click was waiting on. Landed.
>
> A shrinks each foreground read from seconds to milliseconds and C stops the user making it worse
> by clicking again. Neither one changes where that read sits in the queue, which is what this item
> is for.

## Root cause

`GitReadGate` (`GitReadGate.cs:66-92`) is one `SemaphoreSlim(3, 3)`. `SemaphoreSlim` wakes async
waiters FIFO, and the gate passes it `repoId` and `kind` only for *timing* — nothing about a read
reaches the admission decision. So the gate is priority-blind in two separate ways, and they need
separate fixes:

**1. Queue order.** On first launch `StartupSweepCoordinator.MarkActiveReady`
(`StartupSweepCoordinator.cs:58-70`) releases every deferred sweep at once: a status probe per repo
(`RepoStatusStore.cs:168-171`), worktree discovery per primary (`WorktreeSyncService.cs:58`),
submodule discovery per primary and worktree (`SubmoduleSyncService.cs:62`). A read the user just
caused joins the back of that queue. Cold on a spindle, each of those is seconds, so the wait scales
with *how many repos the user has*, not with anything about their own action. This is the dominant
term in the reported "minute or two".

**2. Head-of-line.** Even at the front of the queue, a foreground read waits for one of the three
in-flight reads to finish. Priority ordering does not fix this — reordering the queue does nothing
about work already running. On a cold HDD a full status walk is seconds to tens of seconds, so this
alone can still cost the user a visible lag after A and C have done their part.

## Design

### Priority is derived, not passed

Adding a priority parameter to `Acquire` would put the decision at ~8 call sites, each of which
would have to know whether its repo is the active one — a fact that changes underneath them. Derive
it instead, from one rule in one place:

| Condition | Class |
| --- | --- |
| `kind == Discovery` | `Sweep` |
| `repoId == foreground repo` | `Foreground` |
| otherwise | `Background` |

Discovery is checked first on purpose: enumerating the active repo's worktrees and submodules is
sweep work that nobody is waiting on, even though it is the active repo's. Everything else splits on
"is this the repo on screen".

Note this deliberately does *not* rank by `kind` within a class. After a fetch, `RefsChangedMessage`
fans out a `Sync` read (`RepoStatusStore`) plus `Commits` and `Branches`
(`RepoSnapshotStore.cs:178-185`) — three foreground reads against three permits. There is nothing to
order.

### Classify at admission, not at enqueue

A waiter records `(repoId, kind)` and its class is computed when a permit frees, not when it queues.
This is one line's difference and it removes a whole staleness problem: if the user switches repos
while A's reads are parked, those reads stop being foreground the moment the switch happens, rather
than outranking the new active repo's fan-out on the strength of a stale label.

### One reserved permit

Ordering fixes (1). For (2), reserve one of the three permits for `Foreground`:

- **Total in flight: 3** — unchanged. This is the disk-contention bound and it does not move.
- **Of those, 1 is `Foreground`-only.** `Background` and `Sweep` can hold at most 2 concurrently.
- **Unless there is no foreground repo at all** (as landed). With `SetForegroundRepo(null)` nothing
  can ever classify as `Foreground`, so holding a permit back would idle a slot for a class that
  cannot arrive; the reserve applies only while a repo is active.

The active repo's own switch fan-out is three reads (commits + branches + local) and can still use
all three permits, which is what `MaxConcurrentReads = 3` was sized for in the first place
(`GitReadGate.cs:69-71`). What changes is that a foreground read arriving into a running sweep gets
the reserved permit immediately instead of waiting behind a cold status walk.

**The cost, stated plainly:** with no foreground work in flight, background sweeps run 2-wide
instead of 3-wide, so the startup sweep takes roughly 50% longer to drain. That is the trade being
made — foreground latency bought with background throughput — and it is the same trade
`StartupSweepCoordinator` already makes by deferring those sweeps behind the active repo's first
load. Raising `MaxConcurrentReads` to 4 (3 shared + 1 reserved) would keep background throughput at
today's level, at 33% more peak concurrent reads. **Recommend keeping the total at 3**; the reported
bug is a latency bug, and on the hardware where it reproduces, more concurrent reads is not obviously
faster for anyone.

### Learning which repo is foreground

The gate must not read `IRepoRegistry.Active` itself. Two reasons: it would point `GitBench.Git` at
`Features.Repos`, and `Active` is a reactive `IReadable` whose reads are dependency-tracked — reading
it from a gate waiter's thread would be both a threading hazard and a way to register a spurious
dependency inside whatever `Derived` happens to be evaluating.

Push instead. `IGitReadGate` gains:

```csharp
// The repo the user is looking at: its reads are admitted ahead of every other repo's and keep a
// permit reserved, so they never queue behind a sweep. UI thread only.
void SetForegroundRepo(Guid? repoId);
```

stored in a `volatile Guid` field (plus a `bool` for "none"), read under the gate's lock at
admission. The caller is a new `GitReadPriorityService : IHostedService` in `Features/Repos` whose
whole job is `_registry.Active.Subscribe(repo => _gate.SetForegroundRepo(repo?.Id))`. It could hang
off `RepoSnapshotStore.OnActiveChanged` instead, but that store has a responsibility already and this
is three lines that are easier to test alone.

## Files

| File | Change |
| --- | --- |
| `GitBench/Git/GitReadGate.cs` | Replace the `SemaphoreSlim` with a lock + three FIFO waiter queues + the reserved-permit counter. Add `GitReadPriority`, `SetForegroundRepo`, and the classifier. `Acquire`'s signature and the `Permit`/`LastStatusReadDuration` seams are unchanged. |
| `GitBench/Features/Repos/GitReadPriorityService.cs` | **New.** Subscribes to `IRepoRegistry.Active`, pushes the id into the gate. |
| `GitBench/App/AppServices.cs` | Register the new hosted service. |
| `GitBench.Tests/GitReadGateTests.cs` | Existing tests stay (admission cap, FIFO within a class, timing, disposal); add the priority cases below. |

No call site of `Acquire` changes. That is the point of deriving the class rather than passing it.

## Watch items

- **Starvation.** Strict priority means `Sweep` waits while foreground work keeps arriving. Foreground
  reads are bounded per event (a fan-out per switch, per fetch, per watcher tick), so sweeps drain in
  the gaps — but if a sweep is ever seen to stall indefinitely, the fix is aging (promote a waiter
  after N seconds), not abandoning priority. Do not add aging pre-emptively.
- **Disposal with waiters parked.** `SemaphoreSlim.Dispose` made parked waiters fault, which
  `RepoStatusStore.Read` now catches so the coalescing slot is still released. A hand-rolled gate
  should instead complete parked waiters with a permit whose release is a no-op, or leave them parked
  forever — pick *completing* them, so the same slot-release path runs on shutdown as in normal
  operation. `Disposal_with_a_waiter_pending_does_not_throw_and_leaks_no_permit` covers this.
- **The reserved permit must not deadlock a foreground fan-out.** Three foreground reads must still be
  able to run at once; the reservation is a floor on what foreground can get, never a ceiling.
- **`LastStatusReadDuration` must keep timing only `Status`.** §7's adaptive debounce reads it, and
  `Sync` was already excluded from it when A landed (`GitReadGate.cs`, `GitReadKind.Sync`).

## Acceptance

1. With every permit held by background reads, a foreground read is admitted as soon as one is
   released, ahead of background waiters that queued before it.
2. With two background reads in flight and one queued, a foreground read is admitted **immediately**
   — it takes the reserved permit and does not wait for a release at all.
3. Background and sweep reads never exceed `MaxConcurrentReads - 1` concurrently.
4. Within one class, admission stays FIFO.
5. Switching the active repo re-classifies already-parked waiters (classify-at-admission).
6. On the reported scenario — first launch, many repos, cold cache, click fetch — the ahead/behind
   number and the pull button update in the time one refs-only read takes, not after the startup
   sweep drains.

## Test plan

`GitReadGateTests` drives the gate directly, holding permits in place of real reads — the existing
file's shape, extended:

- `A_foreground_read_takes_the_reserved_permit_while_background_saturates_the_rest`
- `A_foreground_waiter_is_admitted_before_background_waiters_that_queued_first`
- `Background_reads_never_take_the_last_permit`
- `Discovery_for_the_active_repo_is_sweep_not_foreground`
- `Waiters_are_classified_when_a_permit_frees_not_when_they_queued` — park a waiter as foreground,
  switch the foreground repo, assert a waiter for the *new* active repo is admitted first
- FIFO-within-a-class, reusing `A_released_permit_wakes_exactly_one_waiter_in_fifo_order`

Plus one integration-level test in `RepoStatusStoreTriggerTests`: with the gate saturated by
background reads, a `RefsChangedMessage` on the active repo lands without any of them completing.

## Rejected alternatives

- **A priority argument on `Acquire`.** Puts the decision at every call site, where it can drift from
  what is actually active and cannot be re-evaluated when the user switches repos.
- **Priority ordering with no reserved permit.** Fixes queue order, leaves head-of-line: a foreground
  read still waits out one cold status walk. That is exactly the multi-second lag being fixed.
- **Letting foreground reads over-subscribe the cap** (start immediately, no bound). Foreground never
  waits, but peak concurrency doubles to 6 and on a spindle the extra seek contention slows the
  foreground read too. Self-defeating on the one machine where the bug reproduces.
- **A second gate for foreground reads.** Two independent caps means no bound on total concurrency,
  which is the one thing the gate exists to provide.
- **Raising `MaxConcurrentReads`.** Treats a scheduling problem as a throughput problem, and makes
  seek-thrash worse on exactly the hardware that reports the bug.
