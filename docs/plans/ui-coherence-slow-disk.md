# UI coherence on slow disks

> On a 5400 RPM HDD the app visibly de-syncs: the Branches view shows a pending pull while the
> pull button is greyed out, files linger in Unstaged after they are gone, and everything is
> sluggish. None of these are HDD-specific bugs — they are mostly latent races whose windows are
> normally sub-100ms on an SSD and become seconds-wide when every `git status` costs seconds. One
> (A) is not a race at all: it is the same number stored in four places on mismatched refresh
> triggers, and it does not self-correct at any disk speed. This document records the root causes
> found by reading the sync path end to end, and the work items that close them.
>
> **Status: §1, §2, §3, §4, §5, §6, §7, §9, §10 and §11 are implemented (2026-07-24). Only §8 remains
> — analysis only, independent, lowest priority.** Items are ordered by what fixes the reported
> symptoms first; §1+§2 were one seam and landed together, as did §3+§4+§9. §9, §10 and §11 are
> appended rather than inserted in priority order to avoid renumbering.
>
> **Root cause C was re-grounded against the code on 2026-07-24 and its premise was wrong.**
> Index mutations *are* serialized per repo — `GitService` has held a per-repo mutation semaphore
> all along (`GitService.cs:38-81`), and every mutating method goes through it. C has been rewritten
> around what is actually broken at that seam, §5 rewritten to match, and §10 / §11 added for the
> two defects the re-grounding surfaced. **C is now closed: §5 covers C1 + C2, §11 covers C3 and the
> keying half of C4.**
>
> **§2 was re-grounded against the code on 2026-07-24 and its premises held.** Its `--branch`
> composition claim is now verified empirically (hexdump + a byte-for-byte diff of the record
> stream), every citation has been corrected for the drift §5/§11 introduced, and the ingest seam is
> specified as a two-phase reservation with a failure fallback. Three gaps the original text did not
> have are now written down: a one-call ingest writes stale summaries over fresh probes, a failed
> local read must fall back to the probe it replaced (or the skip makes root cause D worse), and
> `RepoRefreshRequestedMessage` must keep its probe. §2 is ready to implement as written.
>
> **§10 was re-grounded against the code on 2026-07-24 and its premises held.** Every citation was
> re-read for the drift §1/§5/§11 introduced in the VM layer and `GitService.cs`, and it is now written
> to the same executable standard as the implemented items (file-by-file change list, watch items,
> acceptance, test plan, rejected alternatives). Three things the original sketch lacked are now
> written down: the cold-store case (the projected slice can be `null` or `Failed` where the deleted
> service read always returned data), the fact that Amend has *two* on-thread reads of which only the
> expensive diff moves off-thread, and that both dialog VMs keep `IGitService` for their mutation.
> **§10 remains open** (analysis, not landed) and is the last symptom-closing item.
>
> **Next up: §10** — the only open item that touches no file any other item does. (§8 remains
> independent and lowest priority.)

## Root causes

### A. Ahead/behind has four sources of truth — *closed by §1*

The same fact is computed by different git processes, launched at different times, refreshed on
different triggers, and stored in different places:

| # | Source | Path | Live |
| --- | --- | --- | --- |
| 1 | `git for-each-ref --format=%(upstream:track)` | `GitService.cs:720,789` → `BranchEntry.AheadBy/BehindBy` → `IRepoSnapshotStore` → `BranchTreeBuilder.cs:92` → `BranchListRow.cs:150` badge | yes |
| 2 | `git status --porcelain=v2 --branch` (`# branch.ab`) | `GitService.cs:1088,1127` → `GitStatusSummary` → `IRepoStatusStore` → toolbar (`ActionsToolbarViewModel.cs:150`), status bar, repo bar, branches header | yes |
| 3 | `RemoteSyncOptimisticMessage` | patched *independently* into both of the above: `RepoSnapshotStore.cs:211` (`OnRemoteSyncOptimistic` → `PatchHeadSync`) **and** `RepoStatusStore.cs:156` (`ApplyOptimisticSync`) | yes |
| 4 | `GitService.GetPushStatus` → `PushStatus` | `GitService.cs:1716`, `IGitService.cs:41` | **dead — zero callers** |

**This is the "branches says I have a pull, the pull button is grey" report.**

It is not only a latency race. The two live sources have *asymmetric trigger sets*, so they can
disagree indefinitely rather than for the length of one reload:

- `RepoStatusStore.Start` (`RepoStatusStore.cs:101-107`) probes on `WorkingTreeChangedMessage` /
  `RefsChangedMessage` / `CommitCreatedMessage` / `RepoRefreshRequestedMessage` / repo-list change.
  It does **not** subscribe to `IRepoRegistry.Active`.
- `RepoSnapshotStore.OnActiveChanged` (`RepoSnapshotStore.cs:157`) serves the **cached**
  `BranchListing` synchronously on a repo switch, then reloads.

So switching to a warm repo paints the sidebar's ahead/behind from a listing loaded minutes ago,
while the toolbar shows a probe that was never re-run for the switch. Both are stale, from
different moments, and nothing is scheduled to reconcile them. The latency window on top of that
is imperceptible on an SSD and seconds on a slow HDD.

`RepoSnapshotStore.PatchHeadSync` (`RepoSnapshotStore.cs:240`) exists specifically to paper over
this, but only fires on the optimistic post-push/pull message, not on the general case — and it
papers by *writing the number twice*, which is the defect rather than the fix.

Two representation defects in the same types feed this:

- `BranchEntry.AheadBy` / `BehindBy` are independent `int?`, so `(null, 3)` is representable, and
  `BranchListRow.cs:150` collapses "no upstream" and "in sync" into the same
  `GetValueOrDefault()`.
- `BranchEntry` serves both local and remote branches, and `UpstreamState` **defaults to
  `Tracked`** (`Branches.cs:18`) — so every remote entry built by
  `GitService.AddRemoteBranch:823` claims to be tracking an upstream it has no concept of. Nothing
  reads it today; nothing stops it.

### B. The watcher silently drops real filesystem events, permanently — *closed by §3, netted by §4*

All four `Schedule*` methods (`RepoWatcher.cs:264-290`) return early when
`IRepoActivityTracker.IsActive` — i.e. whenever any git process is running on that repo, plus a
500ms tail (`RepoActivityTracker.cs:38`). `RepoActivityTracker`'s own comment
(`RepoActivityTracker.cs:22-26`) argues this is safe because "the in-flight reload's `git status`
will see the user's change." The dropped event is **never recovered — there is no reconcile poll
anywhere in the app** (verified: the only periodic timers in `GitBench/` are `UpdateService`'s
release check and `PreferencesService`'s save debounce).

The concrete failure, on a repo where `git status` costs ~3s:

| t | Event |
| --- | --- |
| 0ms | `WorkingTreeChangedMessage` → `RepoSnapshotStore.ReloadLocal` + `RepoStatusStore.Refresh`, two `git status` processes, gate closed |
| ~400ms | the in-flight status stats file `F` |
| 1200ms | user saves `F`. FSW fires. `ScheduleWorkingTree` → `IsOurOwnWrite()` → **dropped** |
| 3000ms | status returns; the snapshot lands *without* the 1200ms edit |
| 3500ms | gate reopens. Nothing pending. `F` is wrong until an unrelated event fires |

Both directions of the reported symptom are this one mechanism: a new edit that never appears, and
a reverted file that never leaves.

The justifying comment is wrong for three independent reasons, only the first of which is a race:

1. **Ordering.** It holds only for edits landing *before* status stats that path. Nothing makes
   that true; on a slow spindle the stat sweep is seconds wide.
2. **The gate is opened by reads that have nothing to do with the dropped channel.**
   `GitProcessRunner.Run`/`RunStreaming` open a scope for *every* invocation
   (`GitProcessRunner.cs:74,110`) — `for-each-ref`, the commit-graph `Load`, `GetStatusSummary`,
   `ListSubmodules`, the identity resolver's config reads, `RepoOperationsStore`'s fetch. An event
   dropped during a `for-each-ref` is covered by no status read at all. And because the channels are
   independent, a dropped `Refs` event is not covered by an in-flight *working-tree* read either.
3. **The 500ms tail is uncovered by construction.** The process has exited; nothing is reading.
   Every event arriving in the tail is dropped against no in-flight anything.

**The recovery path is itself gated.** `OnError` (`RepoWatcher.cs:348-357`) is the handler for FSW
internal-buffer overflow — "events were dropped, reconcile everything" — and it recovers by calling
the same four `Schedule*` methods, so it is dropped by the same gate. Overflow happens precisely
during a checkout or a build touching thousands of files, which is precisely when a git process is
running. The one mechanism designed to recover from mass event loss fails exactly in the case it
exists for.

The gate is also keyed by exact repo path (`PathKey.Normalize`, `RepoActivityTracker.cs:50`) while
`RepoWatcherService` creates a watcher per registry `Repo` — including submodules and linked
worktrees, which are separate `Repo`s over overlapping directory trees. A git read on the parent
closes only the parent's gate. Today the `.git` path filters cover the resulting echoes (see §3),
so this is not a live bug — but it means the gate's granularity and the watcher set's granularity
were never actually matched.

On an SSD a status read is ~50ms, so the gate is closed rarely; on a slow HDD it is seconds, and
reads fire on every tick, so the gate is closed a large fraction of the time. **The drop rate scales
directly with disk latency. This is the phantom-unstaged-files report.**

### C. Mutations *are* serialized per repo — the lane above them throws their results away — *closed by §5 (C1, C2) and §11 (C3, C4's keying)*

> **This section replaced an earlier one whose premise was wrong.** It claimed index mutations run
> unserialized and collide on `index.lock`. They do not: `GitService` holds a per-repo mutation
> semaphore and every mutating method takes it. What follows is what is actually broken at that
> seam, read end to end.

**The serialization that exists.** `GitService._repoLocks` (`GitService.cs:45`) is a static
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by normalized full repo path, with the
OS-appropriate comparer. `LockRepo` (`:69`) takes it; `RunLocked` (`:3033`) wraps *not-a-repo
guard + lock + exception fold*; `RunOperation` / `RunMergeLike` / `RunSimple` (`:3047-3054`) are its
three typed entry points. Sweeping every mutating member of `IGitService`, **all of them** route
through one of those or take `LockRepo` explicitly (`StageSubmodulePointer:2896`). The class comment
(`:38-44`) states the intent exactly — *"Serialize all mutating ops per repo so the call sites can't
race each other — their own UI-busy flags become cosmetic, not correctness guards."* Reads are
deliberately unguarded.

So two fast stage clicks do **not** produce `index.lock`; the second `git add` waits. `index.lock`
from GitBench's own concurrency is not a live defect, and the original §5 acceptance criterion
("rapid stage clicks on a slow disk never produce `index.lock` errors") is already met. What remains
is external git (a terminal, an IDE) — for which `OperationErrorDialog.cs:130` already offers the
lock-removal recovery — and the granularity gap in **C4**.

Four real defects sit on top of that lock:

**C1 — the lane discards mutation results.** `RunIndexMutation` (`LocalChangesViewModel.cs:1067`)
calls `RunOutcome` → `RunBackground`, which *bumps* `_opGen` (`ViewModelBase.cs:86`) and drops the
continuation if the lane moved on (`:103`). That is the correct policy for a **load** — a newer load
supersedes an older one — and the wrong policy for a **mutation**: the git process already ran, the
index already moved, and the continuation is the only thing that carries the error and the change
broadcast. Four ops share `_opGen`: `RunIndexMutation`, `MarkResolved:614`, `StashSelected:712`,
`RunSubmoduleUpdate:654`. Any one supersedes any other.

`DiffViewModel` reached the opposite conclusion for the same class of work and wrote it down
(`DiffViewModel.cs:637-639`): *"Intentionally unguarded: every apply must broadcast a working-tree
change so the optimistic move … reconciles against the truth, so this op does not run through
RunBackground's staleness drop."* The two files disagree about mutation semantics, and the one that
owns the file lists is the one that gets it wrong.

**C2 — a dropped continuation wedges the panel shut.** `_deferStoreReloadUntilWorkingTreeChange`
(`LocalChangesViewModel.cs:88`) gates whether store snapshots land at all (`:848-853`), and its only
clear paths are a cross-repo switch (`:846`) and a `WorkingTreeChangedMessage` for the active repo
(`:172-177`). `RunIndexMutation` and `MarkResolved` broadcast that message unconditionally — but
only if their continuation runs. `StashSelected` and `RunSubmoduleUpdate` never broadcast it on
failure, and `RunSubmoduleUpdate` broadcasts only `SubmodulesChangedMessage` on *success*:

| t | Event |
| --- | --- |
| 0ms | stage `a.txt` → `ApplyOptimisticMove` sets the hold; `RunIndexMutation` bumps `_opGen` to N; `git add` queues on the repo lock |
| 200ms | user hits "Reset submodule to recorded" → `RunSubmoduleUpdate` bumps `_opGen` to N+1 |
| 3s | `git add` returns; continuation posts, `IsStale(N)` → **dropped**. No broadcast, no `OpError` |
| 9s | `git submodule update` returns; broadcasts `SubmodulesChangedMessage` only |
| after | the hold is still set. `OnStoreLocalChanges` early-returns for **every** subsequent snapshot |

The file lists then sit frozen at the optimistic state until *some* `WorkingTreeChangedMessage`
arrives for that repo. Since §4 a focused window self-heals within 30s (the reconcile tick
broadcasts both channels); an unfocused window waits for a real filesystem event or a repo switch.

The milder form needs no second op type at all: two stage clicks in a row means the first one's
failure message is discarded in silence, with its row already moved.

**C3 — the mutation lock spans the network.** `Push` (`:1877`, `RunOperation`), `Fetch` (`:2021`,
`RunSimple`) and `Pull` (`:1978`, `RunLocked`) take the **same** per-repo semaphore as `git add`.
`Fetch` is `fetch --all --prune --recurse-submodules`. So a stage click during a fetch is queued for
the entire fetch — and nothing in the UI says so: the optimistic move paints instantly, and
`RepoOperations.IsFetching` only disables the toolbar's own remote buttons.

For `Pull` the queueing is *correct* — pull rewrites the working tree and takes git's own index lock
anyway. For `Push` and `Fetch` it is not: neither touches the index. They serialize against staging
for no reason, and they hold the lock for a network round-trip rather than a disk one. `LockRepo`
uses a blocking `sem.Wait()` (`:72`), so each queued mutation also parks a thread-pool thread for
the duration — the same pool every store load and status probe dispatches onto.

**C4 — one key for two resources.** The lock key is the working-tree path (`:58-64`). That is right
for the index, which is per worktree, and wrong for refs, which are not: a primary and its linked
worktrees share one ref store (root cause F), so `DeleteBranch` in the primary and
`CheckoutLocalBranch` in a worktree take *different* semaphores and can still collide on git's own
`refs/heads/<name>.lock` / `packed-refs.lock`. Reasoned from the layout, not observed — and git's
ref locking degrades to a failed op with a clear message, not corruption.

There is also an unwritten lock-ordering invariant. `Pull` holds the primary's lock and calls
`ReattachSubmodulesOnBranchTip` (`:1852`), which calls the public, locked `AttachDetachedHead` on a
submodule (`:1862`) — parent lock, then child lock. Nothing nests the other way today
(`StageSubmodulePointer` takes the *parent's* lock and no child lock), so there is no ABBA hazard;
nothing records or enforces that either. `_repoLocks` is also static and never trimmed — one
semaphore per repo path the process has ever touched, which is negligible but unbounded.

### D. A failed probe pins stale values

`RepoStatusStore.cs:196-199` keeps the last known summary when a probe fails. Deliberate, and the
comment explains why, but combined with (A) a transient status failure can pin the toolbar to old
numbers indefinitely while the branches list moves on.

### E. Duplicated work and uncoordinated disk concurrency

Every working-tree tick on the active repo runs **two full working-tree traversals**:

- `status --porcelain=v2 -z --untracked-files=all` for the file lists (`GitService.cs:1057`)
- `status --porcelain=v2 --branch --untracked-files=normal` for the summary (`GitService.cs:1084`)

Same walk, twice, in two processes. On a cold 5400 RPM drive this is the dominant cost, and it is
pure duplication: `--branch` on the first call yields everything the second produces.

There is also no global disk gate. Three independent caps exist and none knows about the others
(citations corrected against current code — §2/§11 moved all three; see §6 for the full re-read):

- `RepoStatusStore` semaphore of 6 (const `RepoStatusStore.cs:65`, field `:79`, acquired `Refresh:231`)
- `StartupSweepCoordinator` semaphore of 4 (const `StartupSweepCoordinator.cs:29`, field `:32`)
- `RepoSnapshotStore` fires the active repo's three slice loads in parallel with **no cap** — the
  loads are `ReloadCommits`/`ReloadBranches`/`ReloadLocal` (`RepoSnapshotStore.cs:161-163`), each
  reaching an uncapped `Task.Run` at `LoadSlice:376` (`WarmSlice:349` for the warm set)

On a single spindle, 6+ concurrent `git status` processes are *slower* than 2 sequential ones —
the cost is seek thrash, not throughput.

### F. A watcher can only name itself, but its `.git` holds other repos' refs — *closed by §9*

`RepoWatcher` is constructed with one `Repo` and every broadcast carries `_repo.Id`
(`RepoWatcher.cs:307,318,329,340`). It has no way to address a sibling. But a primary's `.git`
**is the ref storage for other rows in the registry** — its linked worktrees live at
`worktrees/<name>/`, its submodules at `modules/<name>/`, and each of those is its own `Repo` with
its own `RepoWatcher` (`RepoWatcherService.cs:67-79`).

Worse, those sibling rows have no `.git` watcher of their own. `RepoWatcher` only builds one when
`Directory.Exists(gitDir)` (`:86`), and a linked worktree's `.git` is a *file* (`gitdir: …`), as is
a normal submodule's. **So the primary's `.git` watcher is the only observer of a worktree's refs,
and it is structurally incapable of naming that worktree.**

The classifier resolves this by squashing. `modules/` got a per-file whitelist — only `HEAD`,
`packed-refs`, `refs/` schedule anything, with a comment (`:186-191`) naming the exact feedback-loop
trap that motivated it. **`worktrees/` never got the same treatment**: `StartsWith("worktrees/")`
matches everything under it and fires `ScheduleWorktrees()` unconditionally (`:179-184`).

Two defects fall out of that one asymmetry, in opposite directions:

**F1 — spurious cascade.** A linked worktree has its own `index` at `.git/worktrees/<name>/index`.
Any `git status` in worktree W refreshes it and writes it back. That write lands in the *primary's*
`.git` tree, so:

| Step | |
| --- | --- |
| `git status` in W | scope opened on `W.Path` (`GitProcessRunner.cs:74`) — **P's gate is open**, different `PathKey` |
| writes `P/.git/worktrees/<n>/index` | P's `_gitWatcher` fires |
| `ClassifyGitChange("worktrees/<n>/index")` | `StartsWith("worktrees/")` → `ScheduleWorktrees()` |
| → `WorktreesChangedMessage(P)` | `WorktreeSyncService.ScheduleSync(P)` → `git worktree list` + registry reconcile + state-file save |

So **every working-tree tick in a worktree drags a full worktree rediscovery of the primary behind
it.** It terminates rather than loops — `git worktree list --porcelain` (`GitService.cs:2543`) is a
pure read and `RefreshWorktreeBranches` (`BranchesViewModel.cs:148-172`) is in-memory — but on a
slow spindle this is exactly the "everything is sluggish" complaint, and it is pure waste. The
`index.lock` churn and `worktrees/<n>/logs/` writes ride the same branch.

**F2 — dropped signal.** `worktrees/<n>/HEAD` is *not* `"HEAD"` (`:164` compares the whole relative
path), so an external `git checkout` inside a worktree classifies as `worktrees/` → the *set*
channel. But the worktree's own `RefsChangedMessage` is only ever produced by
`WorktreeSyncService.OnRefsChanged` fanning out from a `RefsChangedMessage(primary)` (`:90-97`) —
which this never fires. Net effect: the sidebar label updates (the registry's `Branch` field is
refreshed by the rediscovery) while **that worktree's branch list, commit graph, and toolbar
ahead/behind do not**. If the checkout swapped between branches at the same commit, no working-tree
event fires either and nothing refreshes at all.

This is the granularity mismatch noted under root cause B, now located: it is not that the activity
gate's key is too narrow. It is that `.git/worktrees/<name>/…` carries *two* meanings — "the
worktree set changed" and "that worktree's refs moved" — and `WorktreesChangedMessage` can only
express the first.

### G. Three git reads run on the UI thread — *closed by §10*

Found while sweeping C's call sites: every `IGitService` call in the app is dispatched through
`Task.Run` / `AsyncCommand` / `RunBackground` — **except three**, which run synchronously on the UI
thread.

| Site | Call | Triggered by |
| --- | --- | --- |
| `AmendSession.Begin:56-57` (method `:42`) | `GetHeadCommitMessage` + `GetAmendStagedFiles` | ticking the Amend checkbox (`LocalChangesViewModel.SetAmend:277`, `Begin` call at `:284`) |
| `DiscardChangesViewModel:45` | `GetLocalChanges` — a full `git status` | `DiscardChangesDialog.Build:30` |
| `StashDialogViewModel:53` | `GetLocalChanges` — a full `git status` | `StashDialog.Build:27` |

(A fourth `GetLocalChanges` caller, `CommitsViewModel.ProbeReset:191`, is *not* a UI-thread site — it
runs inside a `TryRunBackground` `work:` delegate. Verified: it is the only other caller and it is
already dispatched, which is what makes root cause G's "all but three" exact.)

The last two run **inside a widget `Build` pass**, i.e. inside layout. On a repo where `git status`
costs seconds, opening Discard or Stash freezes every window for that long — and it cannot show a
spinner or a skeleton, because the frame that would draw one is the frame that is blocked.

Both dialogs are also re-reading data the app already holds: they are always constructed for the
active repo (`RequestDiscard:677` and `ActionsToolbarViewModel.DoStash:213` / `DoDiscardAll:224` all
read `_registry.Active.Value`), whose `LocalChangesSnapshot` is already in `IRepoSnapshotStore`. This
is not "move the read off-thread" so much as "stop doing the read". Amend's `GetAmendStagedFiles` is a
diff against HEAD~1 that the store genuinely doesn't carry — nor does any store carry HEAD's full
commit *message* body, which `GetHeadCommitMessage` reads (the commit graph's `CommitNode.Summary` is
subject-only) — but the VM already refreshes the staged list asynchronously on every load
(`ReloadAmendStagedThenApply:926`, the `GetAmendStagedFiles` call at `:929`), so the machinery to
populate it a beat later exists.

These are reads, so they never touch C's mutation lock. They do contend for the same spindle as
every background load, which is why they are worst exactly when they hurt most.

---

## Work items

### 1. HEAD's sync counts get exactly one owner — **DONE (2026-07-24)**

> Landed as designed below; the *Implemented* subsection at the end of this item records the
> as-built shape, the two deviations, and what §2 inherits. Sources 3 and 4 are gone, and the two
> live sources are now one.


Delete the second copy rather than reconcile it. `IRepoStatusStore` becomes the sole holder of
HEAD's ahead/behind; the branch listing stops being *able* to answer for the checked-out branch.
Non-HEAD branches keep using `for-each-ref` — `git status` cannot report them.

The enforcement is type-level, not conventional: **the local-branch entry becomes a two-case
union**, and the HEAD case has no count field to fill in wrongly.

```csharp
// Branches.cs
public sealed record BranchSync(int Ahead, int Behind);

// Upstream link for a local branch that is not checked out. Tracked always carries both names and
// a count pair, so there are no nullable-field combinations to get wrong.
public abstract record LocalUpstream
{
    public sealed record None : LocalUpstream;                   // no upstream configured
    public sealed record Gone : LocalUpstream;                   // upstream ref deleted
    public sealed record Tracked(string Remote, string Branch, BranchSync Sync) : LocalUpstream;
}

// Whether HEAD has an upstream ref at all. Deliberately not a count: how far apart they are is
// owned by IRepoStatusStore, which observes it in the same git read that drives the toolbar.
public enum HeadUpstreamState { None, Gone, Tracked }

public abstract record LocalBranchEntry(string Name, string TipSha)
{
    public sealed record Head(string Name, string TipSha, HeadUpstreamState Upstream)
        : LocalBranchEntry(Name, TipSha);
    public sealed record Other(string Name, string TipSha, LocalUpstream Upstream)
        : LocalBranchEntry(Name, TipSha);
}

public sealed record RemoteBranchEntry(string Name, string TipSha);   // no upstream concept
```

`BranchListing.LocalBranches` becomes `IReadOnlyList<LocalBranchEntry>` and still contains **every**
local branch, HEAD included. `Fetched<T>.Ok` / `.Failed` already establishes the nested-case-record
idiom, so this reads as house style. Derived records compare by `EqualityContract`, so
`Head("main", …)` and `Other("main", …)` are never equal and the `KeyedViewModelList` row
reconciliation stays sound.

What each shape rules out:

| Was representable | Now |
| --- | --- |
| HEAD carrying its own ahead/behind | `LocalBranchEntry.Head` has no count field |
| `AheadBy` known, `BehindBy` unknown | one `BranchSync`, both or neither |
| `Tracked` with a null remote or branch name | `Tracked` requires both |
| A remote branch claiming `UpstreamState.Tracked` | `RemoteBranchEntry` has no upstream field |
| Cleanup computing a cleanup kind for the checked-out branch | `LocalUpstream` is only reachable on `Other` |

The split is principled rather than incidental. *Does an upstream ref exist* (None / Gone /
Tracked) is a ref-listing fact `git status` genuinely cannot report — the same reason non-HEAD
branches keep `for-each-ref`. *How far apart they are* is the number that must have one owner. So
HEAD's glyph (`BranchListRow.cs:125`) and name colour (`BranchListRow.cs:58-61`) read
`HeadUpstreamState`; only the badge reads `RepoStatus`.

`BranchTreeBuilder.BuildRows` gains a required `RepoStatus headStatus` parameter and is the single
site where the two halves join:

```csharp
entry switch
{
    LocalBranchEntry.Head     => SyncFrom(headStatus),                        // status store
    LocalBranchEntry.Other o  => (o.Upstream as LocalUpstream.Tracked)?.Sync, // for-each-ref
}
```

where `SyncFrom` is `HasUpstream && !IsDetached ? new BranchSync(Ahead, Behind) : null`. Rows
cannot be built without the status in hand. `LocalBranchRow` carries `BranchSync? Sync` plus a
rendering-level `BranchUpstreamKind` (None/Gone/Tracked); a null `Sync` renders **no** badge, so a
consumer that somehow bypassed the join is silent rather than confidently wrong.

`BranchesViewModel._rowModels` (`BranchesViewModel.cs:102`) reads `IRepoStatusStore.Active.Value`.
Verified this propagates in-frame: `Derived.Value` calls `DependencyTracker.Register`, `Derived`
is itself `IInvalidatable`, and `Recompute` fires synchronously on dependency invalidation — so
the badge and the button move in the same tick.

- **Files:** `Branches.cs` (the types above), `GitService.cs` (`ParseLocalBranch:781`,
  `ParseUpstream:882`, `SplitUpstreamRef:798`, `AddRemoteBranch:808`), `BranchRow.cs`
  (`LocalBranchRow`, `RemoteBranchRow`), `BranchTreeBuilder.cs:24,92`,
  `BranchListRow.cs:58,125,147`, `BranchesViewModel.cs` (inject `IRepoStatusStore`;
  `_rowModels:102`, plus the `IsHead` → pattern-match rename at `:285,681,1055,1064`),
  `RepoSnapshotStore.cs` (delete `_remoteSyncSub:122`, `OnRemoteSyncOptimistic:211`,
  `PatchHeadSync:240-259`), `IGitService.cs` / `GitService.cs` (delete `GetPushStatus` +
  `PushStatus`).
- **Deletions this earns:** sources 3 and 4 disappear entirely — the snapshot store's optimistic
  patch has nothing left to patch, and the optimistic path survives only in
  `RepoStatusStore.ApplyOptimisticSync`, which already exists.
- **Simplifications that fall out:** `IsCleanCandidate:681` collapses a bool guard plus two enum
  comparisons into `b is LocalBranchEntry.Other { Upstream: LocalUpstream.Gone or
  LocalUpstream.None }`, and its `!b.IsHead` check becomes structural rather than filtered;
  `AddFastForwardMenuItem:809-810` drops its `IsNullOrEmpty(UpstreamRemote/Branch)` guards;
  `AddRenameDeleteMenuItems:879-880` drops the `UpstreamState == Tracked ? … : null` dance.
- **Residual invariant:** "at most one `Head` in the list" is not type-enforced. The sole producer
  is `GetBranches:722`, deriving head-ness from `%(HEAD) == "*"`, which git guarantees is unique —
  a one-site invariant at the parse.
- **Acceptance:** the Branches view HEAD badge and the toolbar push/pull enablement cannot
  disagree by more than a frame, and no type in the tree can hold a second copy of HEAD's
  ahead/behind. Closes root cause A.

**Rejected alternative — lifting HEAD out of `LocalBranches` entirely** (a `HeadBranch? Head` field
on `BranchListing`, with the list holding only non-HEAD branches). It buys the same guarantee, but
relocates an invariant rather than removing one: "HEAD carries no counts" becomes compiler-enforced
while "every local-branch scan must union HEAD back in" becomes a new unenforced obligation across
ten sites, five of which break immediately:

| Site | Effect of omitting HEAD from the list |
| --- | --- |
| `RefStillExists:278` | HEAD selection dropped on every `ApplyListing`, and the `CommitSelectedMessage(null)` at `:257` wipes the commits panel's tip highlight. Fires on every `RefsChangedMessage` / `CommitCreatedMessage` / refresh. |
| `ListingHeadIs:285` | Scans for the head entry to clear `PendingHead`; finds nothing, so `PendingHead` never clears and `BranchListRow.cs:38` pins the current-branch highlight to the pending name until a repo switch (`:189`). |
| `LocalBranchExists:543` | Guards `ActivateRemoteBranch:484`. Double-clicking `origin/main` while `main` is checked out opens the create-tracking-branch dialog instead of checking out the existing local. |
| `BranchNamesIn:364` | Expand/Collapse-All misses a folder whose only local branch is HEAD. |
| `ReviewWindowViewModel.cs:299` | Base-ref picker filters by `Session.HeadRef` — the branch *under review*, not the repo's HEAD — so the checked-out branch vanishes from the list. |

`FolderHasCleanCandidates:658` and `BuildCleanCandidates:669` would be correct either way (both
already filter `!b.IsHead`), and `FindLocalBranchEntry:1064` would return null for HEAD — safe
today only because all three of its consumers early-return on `IsHead`, and silently wrong for any
menu item added later. The two-case union keeps every one of these sites working unchanged.

#### Implemented

The type shapes, the join, and the deletions all landed exactly as specified above. Notes on what
differs from the plan text and what the next item inherits:

- **`BranchTreeBuilder.BuildRows(listing, ui, RepoStatus headStatus)`** is the sole join site, with
  `SyncFor` / `UpstreamKindOf` beside it. `EmitTreeRows` became generic over the leaf type with a
  `leafRow` factory — local and remote leaves are no longer the same type — and its `isRemote` /
  `remoteName` pair collapsed into the `BranchScope` it was already deriving.
- **`LocalBranchRow`** carries `BranchUpstreamKind Upstream` + `BranchSync? Sync` (the enum lives in
  `BranchRow.cs`, next to its only consumer). `TrailingFor` returns null on a null `Sync`.
- **`BranchesViewModel`** takes `IRepoStatusStore` and its `_rowModels` `Derived` reads
  `status.Active.Value`. No DI registration changed: the VM is unregistered and `Context.Get<T>`
  constructs it reflectively from registered ctor params.
- **Deleted:** `BranchEntry`, `BranchUpstreamState`, `RepoSnapshotStore.OnRemoteSyncOptimistic` +
  `PatchHeadSync` + `_remoteSyncSub`, `IGitService.GetPushStatus`, `PushStatus`. `GetHeadInfo`
  survives — three other callers.
- **Naming collision, resolved by rename.** `GitBench.Features.Commits.BranchSync` (the commit-graph
  ref-badge tint enum) is now **`RefSyncState`**, freeing the name for the new record. `GitService.cs`
  uses both namespaces, so the alternative was a permanent `using BranchSync = …Commits.BranchSync;`
  alias in the exact file §2 edits.
- **Behaviour delta — upstreams that are not remote-tracking refs.** A branch tracking another
  *local* branch (`branch.X.remote = .`, `%(upstream)` = `refs/heads/…`) now parses as
  `LocalUpstream.None` rather than the old `Tracked`-with-null-names. `Tracked` promising a usable
  remote/branch pair is precisely what lets the fast-forward guards go away, so the type forces this.
  Consequences: such a branch loses its ahead/behind badge and its glyph dims, and it now appears in
  the "Clean…" dialog as *never pushed* — which is arguably accurate (it has no remote), but it is a
  change. Its context menu is unaffected (the old `IsNullOrEmpty` guards already suppressed
  fast-forward for it).
- **Tests:** `GitBench.Tests/BranchTreeBuilderTests.cs` pins the join (HEAD reads the status store;
  siblings read the listing; detached / no-upstream / unprobed all render no badge; in-sync stays a
  real `BranchSync(0,0)` distinct from null) and `GitBench.Tests/BranchListingParseTests.cs` drives
  the real `GitService` against a throwaway repo + bare origin for every upstream case, the
  one-`Head`-per-listing invariant, and the detached-HEAD-has-no-`Head`-entry case. 26 methods.

**The missing trigger landed with §1**, lifted out of §2 (where it was a sub-bullet) because it is
independent of that item's parsing work and is what makes the switch case actually *correct* rather
than merely consistent. `RepoStatusStore.Start` now subscribes to `IRepoRegistry.Active` and refreshes
the newly active repo. Two consequences worth knowing:

- Subscribing fires immediately, so the active repo is probed at `Start` instead of waiting for
  `MarkActiveReady` to release the deferred all-repos sweep — the toolbar and status bar populate for
  the repo the user is looking at without riding the 5s fallback. The cost is that the active repo is
  probed twice at startup (once here, once when the sweep releases); the second supersedes the first
  under the existing `_epoch` guard, and §6's shared read gate absorbs the duplicate.
- `RepoStatusStoreTriggerTests` covers it: switch, initial seed, and switch-back-after-the-repo-moved.
  It drives the real store over real throwaway repos with a real `RepoRegistry`, and deliberately
  never calls `MarkActiveReady` — so the active-repo trigger is the *only* thing that can produce a
  probe, and the test fails outright if it regresses. Only `IUiDispatcher` (a drain-on-demand queue)
  and `IRepoOperationsStore` are faked.

**What §2 inherits.** `BuildRows` takes `RepoStatus` by value and `_rowModels` is a `Derived` over
`IRepoStatusStore.Active`, which is itself a `Derived` over the per-repo probe `State` — so anything
§2 writes into the status store (including its new ingest entry point) reaches the sidebar badge with
no further plumbing. One constraint to respect: the optimistic post-push/pull patch now exists
**only** in `RepoStatusStore.ApplyOptimisticSync`, and `RepoStatusStore` is the sole subscriber to
`RemoteSyncOptimisticMessage`. Do not reintroduce a second patch site in the snapshot store.

### 2. One observation, one timestamp — **DONE (2026-07-24)**

> Landed as designed below. The *Implemented* subsection at the end of this item records the as-built
> shape and the one framework deviation.
>
> Re-grounded against the code on 2026-07-24. Every citation below was re-read (§5 and §11 moved
> `GitService.cs` by a few lines; §1 moved the VM layer), the `--branch` composition claim was
> verified empirically rather than reasoned, and the ingest seam is now specified concretely. **No
> premise turned out wrong.** Three things the original text did not have: the ingest has to be
> *two-phase* (reserve, then publish) or it writes stale summaries over fresh probes; a *failed*
> local read has to fall back to the probe it replaced, or the skip makes root cause D worse; and
> `RepoRefreshRequestedMessage` must keep its probe for the same reason.

§1 unifies the *consumers*; this unifies the *observation*, which is what stops the same disease
recurring between `RepoStatus.IsDirty` (toolbar Stash, `ActionsToolbarViewModel.cs:78`; repo-bar
dot, `RepoNodeViewModel.cs:122-123`) and the file lists those signals are supposed to summarise.

Add `--branch` to the file-list status call and return branch / ahead / behind / dirty alongside
the file lists from that single read, as **fields of one record** — `LocalChangesSnapshot`
(`LocalChanges.cs:5`) gains a `GitStatusSummary Summary`, so there is no constructor that would let
the lists and the summary come from two invocations.

#### Verified, not assumed

Driven against a throwaway repo (`git init` → commit → bare `origin` → `push -u` → a second clone
pushing one commit → local commit → fetch, giving `+1 -1`), with a staged rename, a staged add, an
unstaged modify, a loose untracked file and an untracked directory. git 2.45.1.windows.1. The exact
composed argv — today's file-list argv (`GitService.cs:1057`) with `--branch` appended — hexdumped:

```
# branch.oid 80d18b63a77c3dda719d3516cb33ddfd26dfe1bb\0# branch.head main\0
# branch.upstream origin/main\0# branch.ab +1 -1\0
1 .M N... 100644 100644 100644 <hH> <hI> a.txt\0
2 R. N... 100644 100644 100644 <hH> <hI> R100 b-renamed.txt\0b.txt\0
1 A. N... 000000 100644 100644 <hH> <hI> s.txt\0
? u1.txt\0? untrackeddir/nested/u2.txt\0? untrackeddir/u3.txt\0
```

(line-wrapped here; the real stream has no newlines at all.)

- **The headers are NUL-terminated**, exactly like every other record — `\0` after each of the four,
  no `\n` anywhere. `ParseStatusPorcelainV2`'s existing NUL walk already yields them as records.
- **The record stream is byte-identical** with and without `--branch`. Diffed directly: 665 bytes
  with, 544 without, difference exactly the 121 bytes of the four header records; stripping them
  leaves a byte-for-byte match. So the file lists cannot change, and every `--branch` behaviour is
  purely additive.
- **Headers come first**, which is what the existing "first non-header record ⇒ dirty" rule depends
  on.
- **`--untracked-files=all` recurses** as expected (`untrackeddir/nested/u2.txt` rather than
  `untrackeddir/`).

The degenerate cases, same command:

| Repo state | Headers emitted |
| --- | --- |
| tracked, diverged | `branch.oid` / `branch.head main` / `branch.upstream origin/main` / `branch.ab +1 -1` |
| no upstream configured | `branch.oid` / `branch.head noup` — **no `branch.upstream`, no `branch.ab`** |
| detached HEAD | `branch.oid` / `branch.head (detached)` — no upstream, no ab |
| unborn branch (no commits) | `branch.oid (initial)` / `branch.head master` |
| upstream is a *local* branch (`branch.X.remote = .`) | `branch.upstream main` / `branch.ab +0 -0` — see the note under *Watch* |

So `ParseStatusSummary`'s existing shape already covers all of them: a missing `branch.upstream`
leaves `HasUpstream` false and `Ahead`/`Behind` at 0, and `(initial)` is simply never read (nothing
parses `branch.oid`).

**The `--untracked-files` asymmetry is safe.** The file-list read uses `all`, the summary probe uses
`normal` — a deliberate cost choice documented at `GitService.cs:1069-1073`. `all` is strictly finer
than `normal`: it splits one `? dir/` record into one record per file. Since the dirty bool is "≥1
non-header record", the two modes cannot disagree. Checked the one case that looks like it might —
a directory containing only ignored files, under `--ignored=no` — and **neither** mode emits a
record. Ingesting from the `all` read therefore changes the dirty bool's meaning for no repo state.
Everything else about the two invocations is already identical (`--porcelain=v2 --ignored=no
--ignore-submodules=dirty`).

#### The two parsers

Both live in `GitService.cs` and both must agree about what a `#` line means.

| | `-z` path (file lists) | `\n` path (summary probe) |
| --- | --- | --- |
| Entry | `GetLocalChanges:899` → `RunGitStatusPorcelain:1051` → `ParseStatusPorcelainV2:928` | `GetStatusSummary:1077` → `ParseStatusSummary:1096` |
| Record split | NUL walk, `record.Length > 0` guard, rename records consume a second NUL field | `Split('\n')`, `TrimEnd('\r')`, skip empty |
| A `'#'` line today | `ParseStatusRecord:942` switches on `record[0]` over `?`,`!`,`u`,`1`,`2` — **`#` falls off the end of the switch and is silently ignored** | `:1108` — `line[0] != '#'` returns early with `IsDirty: true`; otherwise three `StartsWith` branches at `:1111-1124` |

The `'#'` fall-through is why adding `--branch` alone is already inert for the file lists: it is not
a latent bug, it is the reason this change is safe. What it costs is that the header data is
*discarded*, which is what the new `case '#'` recovers.

**"Dirty = any non-header record" is real** (`:1094-1095`, `:1108-1109`) and it is what the `-z` path
must reproduce. Compute it in `ParseStatusPorcelainV2`, not inside `ParseStatusRecord`: the rule is
"any non-empty record whose first char is not `'#'`", which is exactly the `\n` path's rule
including its treatment of a `!` (ignored) record — the switch's defensive `case '!'` would
otherwise silently disagree with the probe. `--ignored=no` means neither ever sees one, and that is
precisely why the two must not be allowed to drift apart in an unreachable branch.

The shared helper is one struct plus one function — a fold over headers, with the dirty bool and the
final construction left to each caller because that is the only thing they legitimately do
differently:

```csharp
// The `# branch.*` headers of a porcelain-v2 status, accumulated. Shared by the -z file-list read
// and the \n summary probe so the two cannot drift: same headers, same order, same meaning.
private struct StatusBranchHeaders
{
    public string? Branch;
    public bool IsDetached;
    public bool HasUpstream;
    public int Ahead;
    public int Behind;

    public readonly GitStatusSummary ToSummary(bool isDirty) =>
        new(Branch, IsDetached, HasUpstream, Ahead, Behind, isDirty);
}

private static void ApplyStatusHeader(string line, ref StatusBranchHeaders h) { /* :1111-1124 */ }
```

`ParseStatusSummary` becomes the walk plus `h.ToSummary(dirty)`; `ParseStatusPorcelainV2` gains a
`ref StatusBranchHeaders` and a `ref bool dirty`, and `ParseStatusRecord` gains
`case '#': ApplyStatusHeader(record, ref h); break;`. `ParseAheadBehind:1131` is unchanged and is
called from the helper.

#### The ingest seam

`RepoStatusStore` keeps sole ownership of its per-repo slot (`_probe`, `RepoStatusStore.cs:66`) and
gains a second *input*, not a second slot: a write entry point `RepoSnapshotStore` calls when a
local-changes load lands, ordered by the same per-repo `_epoch` (`:67`) that already orders probes
against each other.

**It has to be two-phase.** `Refresh:176` takes the repo's next epoch *when the read is dispatched*
(`:182-183`) and checks it *when the result lands* (`:195`). An ingest that took its epoch on
landing would be claiming to be the newest observation when it is often the oldest:

| t | Event |
| --- | --- |
| 0ms | `WorkingTreeChangedMessage` → `ReloadLocal` starts a 3s `git status` |
| 400ms | a fetch lands → `RefsChangedMessage` → `Refresh` bumps `_epoch` to N+1, probe starts |
| 900ms | the probe lands, epoch matches, writes the **post-fetch** ahead/behind |
| 3000ms | the local read lands. A one-call ingest bumps to N+2 and writes a summary observed **before** the fetch |

So the seam is reserve-then-publish, mirroring `Refresh` exactly:

```csharp
/// The write side of IRepoStatusStore's per-repo slot, for a summary observed by another store's
/// git read. Two phase because the ordering is decided when the read *starts*, not when it lands:
/// Reserve takes the repo's next probe epoch exactly as an internal probe would, and Publish is
/// dropped if a newer probe or reservation has happened since. Deliberately not a member of
/// IRepoStatusStore — that interface is the read seam five view models hold, and none of them writes.
internal interface IRepoStatusIngest
{
    int Reserve(Guid repoId);
    void Publish(Guid repoId, int reservation, GitStatusSummary? summary);
}
```

`Publish`, on the UI thread like every other write to `_probe`:

1. `if (_disposed) return;`
2. drop if `_epoch[repoId] != reservation` — a newer observation owns the slot;
3. `if (summary == null) { Refresh(repoId); return; }` — the read that was going to carry the
   summary failed; run the probe it replaced;
4. `Probe(repoId).Value = summary;`

**Step 3 is what keeps the skip honest.** Root cause D is the deliberate policy at
`RepoStatusStore.cs:196-199`: a failed probe keeps the last known summary rather than zeroing it,
because zeroing would silently grey out push/pull. Ingest must honour that — but if it merely
declined to write, a repo whose file-list read keeps failing would have *no* path back to a fresh
summary on the working-tree channel, because the probe that used to run there has been skipped. The
fallback restores exactly today's cost profile (one status process per tick) in exactly the failure
case, so the skip is never worse than what it replaces.

**Where it hooks in.** `RepoSnapshotStore.LoadSlice:351` is generic over three slices; only the local
one ingests. Give it an optional landing hook and let `ReloadLocal:285` be the site that names the
coupling:

```csharp
private void ReloadLocal(Repo repo)
{
    // Reserve the status slot's next epoch before the read starts, so this observation orders
    // against concurrent probes the same way two probes order against each other.
    var reservation = _statusIngest.Reserve(repo.Id);
    LoadSlice(repo, _localLane, _localCache, _local, LoadLocalChanges,
        onLanded: result => _statusIngest.Publish(repo.Id, reservation, SummaryOf(result)));
}

private static GitStatusSummary? SummaryOf(Fetched<LocalChangesData>? result)
    => (result as Fetched<LocalChangesData>.Ok)?.Value.Snapshot.Summary;
```

`onLanded` fires in the posted continuation **next to `cache.Set:372`, before the `lane.IsStale:373`
early-return**. The lane's staleness answers "is this still the active repo's newest load", which is
a different question from "is this the newest observation of *this repo*" — the status store keys by
repo id and holds slots for repos that are not active, so a lane-stale result is still a legitimate
observation, and its own reservation is what decides whether it wins.

The one place ingest must *not* happen is `OnActiveChanged`'s cached serve
(`RepoSnapshotStore.cs:154-156`): those three lines hand out a `LocalChangesData` loaded minutes ago.
The hook is on `LoadSlice`'s continuation, so this is structural rather than conventional — there is
no code path from `_localCache.TryGet` to `Publish`.

#### Skipping the now-redundant probe

`RepoStatusStore.Start:98` subscribes to five channels (`:102-106`) plus the repo list (`:108`) and,
since §1, `IRepoRegistry.Active` (`:113-116`). It knows which repo is active the same way everything
else does: `_registry.Active.Value`. Match each channel against what `RepoSnapshotStore` actually
does for the active repo:

| Channel | Snapshot store, active repo | Probe |
| --- | --- | --- |
| `WorkingTreeChangedMessage` | `OnWorkingTreeChanged:195` → `ReloadLocal` | **skip** — ingest carries it |
| `CommitCreatedMessage` | `OnCommitCreated:178` → commits + branches + **local** | **skip** — ingest carries it |
| `RefsChangedMessage` | `OnRefsChanged:163` → commits + branches, **no local** | **keep** — a fetch moves ahead/behind without touching the working tree |
| `RepoRefreshRequestedMessage` | `OnRefreshRequested:207` → all three incl. local | **keep** — this is the user's explicit retry *after a failed load*, and a failed load ingests nothing. One extra read on a rare, user-initiated action buys D's recovery path |
| `RemoteSyncOptimisticMessage` | — | unchanged (`ApplyOptimisticSync:165` patches the slot directly and takes no epoch) |
| repo added / list reset | — | unchanged |
| `IRepoRegistry.Active` | `OnActiveChanged:134` → all three incl. local | **keep** — see below |

So the skip is one guard on two subscriptions:

```csharp
// The active repo's file-list reload is already running a `git status` that carries the summary,
// and RepoSnapshotStore ingests it. A probe here would be the same working-tree walk twice.
private void RefreshUnlessActive(Guid repoId)
{
    if (_registry.Active.Value?.Id == repoId) return;
    Refresh(repoId);
}
```

The `Active` trigger keeps its probe deliberately: on a switch the snapshot store serves the *cached*
local slice synchronously before reloading, so the toolbar would otherwise paint from a cached
observation until the new load lands — which is the exact defect §1 added that trigger to close.
Both a probe and an ingest are then in flight for the newly active repo; the reservation ordering
decides, and whichever is newer wins.

**Interactions checked:**

- **§4's `RepoReconcileService`** broadcasts `WorkingTreeChangedMessage` then `RefsChangedMessage`
  for the active repo every 30s (`:95-96`), synchronously and in that order, and skips its tick
  entirely while a git read is in flight on that repo (`:93`). After §2 a tick costs one file-list
  read (carrying the summary) plus one refs probe, down from two status processes plus a refs probe.
  The refs probe is redundant *on a reconcile tick specifically* — see *Residual* below.
- **§5's `MutationEffects`** — every channel it can broadcast lands on a message the table above
  covers. `Index(...)` → `WorkingTreeChangedMessage(IndexOnly: true)`; neither store branches on
  `IndexOnly`, so `ReloadLocal` runs and the ingest carries. `WorkingTree(...)` → plain
  `WorkingTreeChangedMessage`. `Commit(...)` → `CommitCreatedMessage`, **including the rejected-commit
  case** §5 introduced — `OnCommitCreated` reloads local unconditionally, so a hook that rewrote the
  tree and then failed still refreshes the summary. `AndRefs()` adds `RefsChangedMessage`, whose
  probe is kept.
- **Broadcast order is not load-bearing.** `MessageBus.Broadcast` is synchronous in subscription
  order, and the snapshot store starts first (`AppServices.cs:112` before `:114`) — but the skip
  reads `_registry.Active.Value`, which both handlers see identically regardless of order. That is
  the reason to key the skip on *active* rather than on "is an ingest already reserved" (see
  *Rejected alternatives*).
- **A non-active, non-warm repo** gets `WorkingTreeChangedMessage` from its watcher and no
  `ReloadLocal` at all — which is exactly why the skip must be active-only.
- **`SubmodulesChangedMessage`** reloads local for the active repo (`OnSubmodulesChanged:217`) and
  the status store does not subscribe to it. Today the summary does not refresh on that message;
  after §2 it will, for free.

| Was representable | Now |
| --- | --- |
| The file lists and the dirty dot describing different moments | one `git status`, one record — `LocalChangesSnapshot` has no shape that carries lists without a summary |
| The toolbar's ahead/behind and the file lists coming from two processes | on the active repo the status slot is written by the read that built the lists |
| An ingested summary landing on top of a newer probe | the reservation is taken when the read starts, so ingest and probe order under one `_epoch` |
| A `'#'` record silently ignored on one path and meaning "dirty" on the other | one `ApplyStatusHeader`, one dirty rule, both parsers |
| A summary written from `OnActiveChanged`'s cached slice | the hook is on `LoadSlice`'s continuation; the cache path cannot reach it |
| A failed read leaving the summary with no way back, now that its probe is gone | `Publish(null)` runs the probe it replaced |

- **Files:**
  - `GitService.cs` — add `--branch` to the file-list argv (`RunGitStatusPorcelain:1051`, argv at
    `:1057`); add `case '#'` to `ParseStatusRecord:942`; thread headers + dirty through
    `ParseStatusPorcelainV2:928`; extract `StatusBranchHeaders` + `ApplyStatusHeader` out of
    `ParseStatusSummary:1096` and re-express both parsers on them; build the `Summary` into the
    snapshot at `GetLocalChanges:917`. `GetStatusSummary:1077` and its `normal`/`\n` argv are
    unchanged — it is still the all-repos probe.
  - `LocalChanges.cs:5` — `LocalChangesSnapshot` gains `GitStatusSummary Summary`; add a
    `static LocalChangesSnapshot Empty(Guid repoId)` for the one placeholder construction site
    (`StashDialogViewModel.cs:55`) so "empty" is named rather than spelled out as
    `GitStatusSummary.Unknown` at a call site. Needs `using GitBench.Git;`.
  - `IGitService.cs` — no signature change (`GetLocalChanges:29` already returns
    `Fetched<LocalChangesSnapshot>`); update the `GitStatusSummary` doc comment at `:170-173`, which
    currently says the type is read from one specific probe.
  - `RepoStatusStore.cs` — add `internal interface IRepoStatusIngest` and implement `Reserve` /
    `Publish` beside `Refresh:176`; add `RefreshUnlessActive` and point the `WorkingTreeChangedMessage`
    (`:102`) and `CommitCreatedMessage` (`:104`) subscriptions at it; make `Dispose:211` idempotent
    (`if (_disposed) return;`) since the store is about to be reachable under a second interface.
  - `RepoSnapshotStore.cs` — inject `IRepoStatusIngest`; `LoadSlice:351` gains
    `Action<T?>? onLanded = null`, invoked next to `cache.Set:372` and before `lane.IsStale:373`;
    `ReloadLocal:285` reserves and publishes. `WarmLocal:324` / `WarmSlice:331` are deliberately
    untouched — see *Known gap*.
  - `AppServices.cs` — register `IRepoSnapshotStore` through a factory that casts
    `ctx.Require<IRepoStatusStore>()` to `IRepoStatusIngest`, the same pattern `GitIdentityService`
    already uses at `:66-68`. **Do not** register `IRepoStatusIngest` as its own singleton: the
    container adds every factory result to `_owned` (`Context.cs:186-187`), so a delegating
    registration would dispose `RepoStatusStore` twice. Registration order does not matter —
    factories resolve lazily — but note that `IRepoSnapshotStore` is *started* first (`:112` before
    `:114`), so at startup the first local read reserves before `RepoStatusStore.Start` has run its
    own active-repo probe; the probe reserves later and wins. Correct, and unchanged from §1's
    "probed twice at startup" note.
  - Call sites of `GetLocalChanges` that construct or destructure the snapshot:
    `CommitsViewModel.cs:190` (reset probe), `DiscardChangesViewModel.cs:45`,
    `StashDialogViewModel.cs:53-55`, `GitBench.Tests/AmendUnstageTests.cs:58`,
    `GitBench.Tests/GitPathspecTests.cs:54`. All read `Staged`/`Unstaged` only; the record gains a
    field, so only the one `new LocalChangesSnapshot(...)` needs touching.
- **Also fixes:** halves per-tick disk work for the active repo — root cause E, first half.
- ~~**Also fixes:** the missing trigger from root cause A — probe on active-repo change, so a switch
  never leaves the toolbar showing a probe from before the switch.~~ **Done — landed with §1**
  (`RepoStatusStore.Start` subscribes to `IRepoRegistry.Active`); see §1's *Implemented* notes.
- **Watch:** the skip's condition and `RepoSnapshotStore.OnWorkingTreeChanged:198`'s condition are
  the same predicate written in two files. If one grows a qualifier the toolbar silently stops
  updating. Pin it with a test that drives both real stores over one bus, not with a comment.
- **Watch:** ingest on a *load* result only, never on the cached value `OnActiveChanged:154-156`
  serves, or a switch-back writes a stale summary over a fresher slot.
- **Watch:** the dirty bool is "any non-header record"; that stays correct only once `'#'` is handled
  explicitly rather than falling off the end of `ParseStatusRecord`'s switch, and only if the rule
  lives in `ParseStatusPorcelainV2` (so `!` records count as dirty on both paths, matching
  `ParseStatusSummary`).
- **Watch:** `--branch` makes git compute ahead/behind on the file-list read. That is a two-tip
  revision walk, negligible beside a full `--untracked-files=all` lstat sweep, and it replaces a
  whole second `status` process — but it is now paid by three UI-thread readers until §10 lands
  (`DiscardChangesViewModel.cs:45`, `StashDialogViewModel.cs:53`, `CommitsViewModel.cs:190`).
- **Watch — pre-existing, surfaced by the verification.** A branch whose upstream is another *local*
  branch (`branch.X.remote = .`) reports `# branch.upstream main` and `# branch.ab +0 -0`, so
  `RepoStatus.HasUpstream` is true — while §1's `ParseUpstream` (`GitService.cs:871-878`, and its
  comment at `:868-870`) deliberately maps it to `LocalUpstream.None` / `HeadUpstreamState.None`.
  For such a HEAD the sidebar glyph dims (from the listing) while the badge shows `0/0` (from the
  status store, via `BranchTreeBuilder.SyncFor:89`). Pre-existing since §1 and not made worse here —
  §2 changes only *which read* fills the slot, not what the fields mean. Worth a follow-up; do not
  fold it into this item.
- **Known gap:** warm repos still double up. "Non-active ⇒ no file lists" is **false** — the warm set
  (up to four non-active repos, `RepoSnapshotStore.cs:79`) does load them, via `WarmLocal`. Ingesting
  from `WarmSlice` would be perfectly orderable, but it buys nothing while the warm probe still runs,
  and skipping *that* probe needs warm-set knowledge inside the status store. Leave both to §6 rather
  than leak it, and keep ingest on `ReloadLocal` only so "ingest exists exactly where a probe was
  removed" stays true.
- **Residual:** a §4 reconcile tick pays one probe it does not need. It broadcasts working-tree then
  refs; the working-tree half is skipped and ingests, the refs half probes. Suppressing that would
  mean the status store knowing an ingest is already reserved for the repo — the ordering-dependent
  design rejected below. ~30s cadence on the active repo only; not worth the coupling.
- **Acceptance:**
  - The active repo's file lists and its ahead/behind/dirty signals can never originate from two
    different git invocations.
  - A working-tree tick on the active repo runs **one** `git status` process, not two.
  - `GetLocalChanges(repo).Summary` and `GetStatusSummary(repo)` agree for every repo state.
  - A failed file-list load still refreshes the summary (or provably keeps the last known one), and
    never silently pins it.
- **Test plan** — real throwaway repos, the house style of `BranchListingParseTests` /
  `RepoStatusStoreTriggerTests`:
  - **`GitStatusSummaryParseTests`** (new), driving the real `GitService` against a throwaway repo
    wired to a throwaway bare origin, as `BranchListingParseTests` does. One test per row of the
    degenerate-cases table above (tracked/diverged, no upstream, detached, unborn, clean, dirty by
    untracked only, dirty by staged only, dirty by unmerged), each asserting the parsed `Summary`.
    Plus the item's whole point as one assertion: **`GetLocalChanges(repo).Summary` equals
    `GetStatusSummary(repo)`** in each of those states.
  - **The file lists are unchanged by `--branch`:** a rename + add + modify + untracked-tree fixture
    asserting `Staged`/`Unstaged` exactly, so a header can never leak into either list and the
    rename record's second NUL field is still consumed correctly.
  - **`RepoStatusStoreTriggerTests`** gains the ordering and skip cases, with a counting decorator
    around the real `IGitService` (the existing tests use the real one directly): a
    `WorkingTreeChangedMessage` for the **active** repo produces **zero** `GetStatusSummary` calls;
    the same message for a **non-active** repo produces one; `RefsChangedMessage` for the active repo
    produces one; `RepoRefreshRequestedMessage` for the active repo produces one; a `Publish` whose
    reservation was superseded by a later `Refresh` does not write; and `Publish(repoId, r, null)`
    produces a probe.
  - **One integration test over both real stores**, a real `MessageBus`, real repos and the
    drain-on-demand dispatcher: make a real working-tree change, broadcast
    `WorkingTreeChangedMessage` for the active repo, and assert the *status store's* `Active` reflects
    it. That is the test that fails if either store's active-repo condition drifts from the other's,
    which is the watch item above expressed as an assertion.
- **Note:** ~~land with §1~~ — §1 is done. §2 is now independently shippable; it depends on nothing
  §1 left unfinished, and §1's *What §2 inherits* note still holds (anything written into the status
  store reaches the sidebar badge with no further plumbing).

**Rejected alternative — a one-call ingest that takes its epoch on landing.** Shown above to write a
pre-fetch summary over a post-fetch probe whenever the file-list read is slower than the probe, which
on a slow spindle is the common case, not the corner. The reservation is two lines; the alternative
is the same class of defect this whole document is about.

**Rejected alternative — key the skip on "is an ingest already reserved for this repo" rather than
on "is this repo active".** Strictly better in one way: it would cover the warm set too, closing the
*Known gap* without teaching the status store what the warm set is. It works only because
`RepoSnapshotStore` subscribes before `RepoStatusStore` and `MessageBus.Broadcast` is synchronous in
subscription order — i.e. it makes correctness depend on the order of two lines in `AppServices.cs`
(`:112`, `:114`) with nothing at either site saying so. Reading `_registry.Active.Value` gives both
handlers the same answer regardless of order. Revisit if §6 makes the warm-set double-read matter
enough to pay for making the ordering explicit.

**Rejected alternative — a second observable slot** ("last summary observed by the file-list read"),
merged with the probe slot by consumers. That is root cause A's exact shape: two holders, two trigger
sets, and a merge rule with no owner. One slot with two ordered writers is the whole point.

**Rejected alternative — put `Reserve`/`Publish` on `IRepoStatusStore`.** It is the read seam five
view models hold (`ActionsToolbarViewModel:55`, `StatusBarViewModel:64`, `BranchesHeaderViewModel:18`,
`BranchesViewModel:81`, `RepoNodeViewModel:94`). Adding a write method to it makes "nothing else in
the app may hold a second copy" (`RepoStatusStore.cs:25-27`) a comment again rather than a shape.

**Rejected alternative — let `RepoStatusStore` own the file-list read as well**, so one store runs
one read and publishes both outputs. Arguably the endgame, and it would delete the seam entirely.
But the two reads have genuinely different policies: bounded-to-active+warm versus all-repos,
different concurrency caps, a cache with a warm set versus a single value per repo, and opposite
failure semantics (a failed probe deliberately keeps the last value; a failed file-list load renders
its error). Merging them means picking one of each. Revisit after §6, when both are behind one gate
and the caps stop being a difference.

#### Implemented

The parser split, the `LocalChangesSnapshot.Summary` field, the two-phase ingest and the two skipped
subscriptions all landed as specified. Notes on the as-built shape:

- **`GitService.cs`** — `--branch` on the file-list argv; `StatusBranchHeaders` struct +
  `ApplyStatusHeader` extracted out of `ParseStatusSummary`, and **both** parsers re-expressed on
  them so the `-z` and `\n` paths share one header fold. `ParseStatusPorcelainV2` threads
  `ref StatusBranchHeaders` + `ref bool dirty` and computes dirty as "any non-empty, non-`#` record"
  there — not in `ParseStatusRecord`, which gained only `case '#': ApplyStatusHeader(…)`.
  `GetLocalChanges` builds `headers.ToSummary(dirty)` into the snapshot. `GetStatusSummary` and its
  `normal`/`\n` probe are untouched — still the all-repos probe.
- **`LocalChangesSnapshot`** gains `GitStatusSummary Summary` + `static Empty(Guid)` for the one
  placeholder site (`StashDialogViewModel`). No `GetLocalChanges` consumer that only reads
  `Staged`/`Unstaged` needed touching.
- **`RepoStatusStore`** — `internal interface IRepoStatusIngest { int Reserve; void Publish }`
  implemented on the store beside `Refresh`; `Refresh`'s own epoch bump now routes through `Reserve`
  so ingest and probe order under one `_epoch`. `Publish` drops on disposed / superseded reservation,
  calls `Refresh(repoId)` on a null summary (the root-cause-D fallback), else writes the slot.
  `RefreshUnlessActive` guards the `WorkingTreeChangedMessage` and `CommitCreatedMessage`
  subscriptions; `RefsChangedMessage` / `RepoRefreshRequestedMessage` keep their probe. `Dispose` is
  idempotent.
- **`RepoSnapshotStore`** — `LoadSlice` gains `Action<T?>? onLanded`, invoked next to `cache.Set`
  and before the `lane.IsStale` return; `ReloadLocal` reserves before the read and publishes
  `SummaryOf(result)` in the hook. `WarmLocal` / `WarmSlice` and `OnActiveChanged`'s cached serve are
  untouched, so ingest exists exactly where a probe was removed.
- **Framework deviation — a new hosted-service overload.** The doc's "factory that casts
  `ctx.Require<IRepoStatusStore>()` to `IRepoStatusIngest`" could not use the existing
  `AddHostedService<T>(factory)`, whose `TService : IHostedService` constraint `IRepoSnapshotStore`
  does not meet. Added `AddHostedService<TService, TImpl>(Func<Context, TImpl>)` in
  `framework/ZGF.Gui/Context.cs` — registers the factory under the interface, marks it hosted, one
  factory result so one `_owned` entry and one dispose. `AppServices` registers `IRepoSnapshotStore`
  through it. **This edit is in the `framework` submodule**, not the app.
- **Tests:** `GitStatusSummaryParseTests` (new) — one method per degenerate-cases row, each asserting
  the parsed `Summary` and that `GetLocalChanges(repo).Summary == GetStatusSummary(repo)`; plus a
  rename+add+modify+untracked fixture pinning that `--branch` leaves `Staged`/`Unstaged` byte-for-byte
  unchanged. `RepoStatusStoreTriggerTests` extended with the skip/ordering cases over a
  `CountingGitService` decorator (active WT → 0 probes, non-active WT → 1, refs active → 1, refresh
  active → 1, superseded `Publish` no-write, `Publish(…, null)` → probe). `StatusIngestIntegrationTests`
  (new) drives both real stores + a real `MessageBus` + drain-on-demand dispatcher and asserts the
  status store's `Active` reflects an active-repo working-tree change *only* via ingest. Full suite:
  439 passing (GitBench.Tests), 375 passing (ZGF.Gui.Tests).

### 3. Never drop a watcher signal; defer it — **DONE (2026-07-24)**

> Landed as designed below. The *Implemented* subsection at the end of this item records the
> as-built shape and the one deviation.

Move the gate from the **arrival** path to the **drain** path. Arrival becomes unconditional — it
sets a per-channel `Pending` bit and arms the debounce. The drain consults the gate and, if git is
still running, re-arms rather than broadcasting. The bit survives.

That inversion is the whole item, and it is what makes the defect unrepresentable: today there is a
code path from *event arrived* to *nothing ever happens*; afterwards there is none, because nothing
between the FSW callback and the broadcast is allowed to clear `Pending` except an actual broadcast.

The four channels are four copies of the same seven-line method, so this collapses rather than
grows the file — one `Channel`, one `Schedule`, one drain, a four-element array:

```csharp
// One debounce channel. Arrival always sets Pending; the activity gate can only postpone the
// drain, never cancel it.
private sealed class Channel
{
    public Timer Debounce = null!;
    public Action<Guid> Broadcast = null!;
    public bool Pending;                    // guarded by _timerLock, like every Timer.Change here
}

private void Schedule(Channel ch)
{
    if (Volatile.Read(ref _disposed) != 0) return;
    lock (_timerLock)
    {
        if (_disposed != 0) return;
        ch.Pending = true;
        ch.Debounce.Change(DebounceMs, Timeout.Infinite);
    }
}

private void OnDebounce(Channel ch)
{
    if (Volatile.Read(ref _disposed) != 0) return;
    lock (_timerLock)
    {
        if (_disposed != 0 || !ch.Pending) return;
        // Git is still writing: postpone, don't discard. Re-arming polls at debounce
        // granularity, bounded by the tracker's own 500ms quiet tail.
        if (IsOurOwnWrite()) { ch.Debounce.Change(DebounceMs, Timeout.Infinite); return; }
        ch.Pending = false;
    }
    var repoId = _repo.Id;
    _dispatcher.Post(() => { if (Volatile.Read(ref _disposed) == 0) ch.Broadcast(repoId); });
}
```

**The re-arm answers §3's original "needs a notify-when-quiet hook" question: it doesn't.**
`RepoActivityTracker` is unchanged — no event, no callback, no new subscription lifetime to get
wrong. The debounce timer already exists and already re-arms; letting the drain re-arm itself is
strictly less machinery than a quiet-notification, and it degrades correctly under sustained git
activity (a repo whose gate stays closed for 10s simply broadcasts once, 10s late — which is the
right answer, not a storm of ten reloads).

What each shape rules out:

| Was representable | Now |
| --- | --- |
| An FSW event that produces no broadcast, ever | `Schedule` sets `Pending` unconditionally; only the drain is gated, and a gated drain re-arms |
| `OnError`'s mass-loss recovery being dropped by the same gate that caused the loss | `OnError` sets all four `Pending` bits; they cannot be cleared without a broadcast |
| A debounce firing with nothing to deliver (a `Change` that raced a completed drain) | drain early-returns on `!Pending` |
| The gate and the disposal re-check disagreeing about which lock they run under | one `lock (_timerLock)` block owns `Pending`, the gate read, and the `Change` together |

- **Files:** `RepoWatcher.cs` only. `RepoActivityTracker.cs` is untouched. §9 edits the same file's
  `ClassifyGitChange` and calls the `Schedule*` methods this item collapses — land them together.
- **Deletions this earns:** `ScheduleWorkingTree` / `ScheduleRefs` / `ScheduleWorktrees` /
  `ScheduleSubmodules` (`:264-290`), `ArmDebounce` (`:295`), and the four `On*Debounce` bodies
  (`:304-346`) — twelve members, all near-identical, become three.
- **Acceptance:** an edit made while a git read is in flight still produces exactly one debounced
  reload after the read completes, and an FSW buffer overflow during a checkout still reconciles all
  four channels. Closes root cause B.
- **Watch:** `_dispatcher.Post` must stay *outside* `_timerLock` — the drain currently posts with no
  lock held, and taking the UI dispatcher's queue under a lock that FSW threadpool callbacks also
  contend for is a deadlock shape.
- **Open question — resolved by reading, still worth one empirical check.** The original question
  was whether the read-side gate could be narrowed away entirely rather than deferred. Walking every
  branch of `ClassifyGitChange` (`:153-219`) against what a read actually writes:

  | Read-side write | Classifier outcome |
  | --- | --- |
  | `.git/index` (stat-cache refresh — `status`, libgit2) | falls through every branch → ignored (`:218`) |
  | `.git/index.lock` create/delete | same — not `HEAD`/`refs/`/`worktrees/`/`modules/` → ignored |
  | `.git/modules/<n>/index` (`git submodule status`) | explicitly ignored (`:209-216`) |
  | `.git/objects/**`, `.git/logs/**` | ignored (`:218`) |
  | an embedded submodule's own `<sub>/.git/index` | tree watcher's `IsUnderGit` matches `.git` as a segment anywhere (`:229-242`) → excluded |

  So for reads the echo is already filtered on path alone, and the gate is redundant. The one
  residual is `git gc --auto`, which git may fire after a read and which rewrites `packed-refs` →
  `ScheduleRefs` → a spurious (but harmless and self-limiting) refs reload. **Not verified
  empirically** — verify before removing the gate. Deferring rather than dropping is safe either
  way and does not depend on this answer, so land the deferral first and treat gate removal as a
  separate, optional follow-up.

#### Implemented

The `Channel` sketch landed verbatim. Notes on the as-built shape:

- **Twelve members became five.** `Channel` + `NewChannel` + `Schedule` + `Drain` +
  `ScheduleAllChannels` replace the four `Schedule*`, `ArmDebounce`, the four `On*Debounce` bodies,
  and `OnError`'s body. The four channels are named fields *and* a `Channel[] _channels`; the array
  is what `ScheduleAllChannels` and `Dispose` iterate, the fields are what the classifier names.
- **`IsOurOwnWrite()` now runs inside `_timerLock`** (it used to run outside, on the arrival path).
  Lock order is `_timerLock` → the tracker's own lock, and the tracker never calls back into the
  watcher, so there is no inversion. `_dispatcher.Post` stayed outside the lock as required.
- **Deviation — two internal members for the tests.** `ClassifyGitChange` widened from `private` to
  `internal`, and `OnError`'s body was lifted into `internal void ScheduleAllChannels()`. Both are
  driven directly by `RepoWatcherDeferralTests` / `RepoWatcherClassifierTests`: FSW buffer overflow
  cannot be provoked deterministically, and driving §9's classification through real file writes
  would test the OS's event coalescing rather than the classifier.
- **`RepoActivityTracker` is behaviourally untouched**, but its class comment was rewritten — it
  claimed dropped events were "acceptable because the in-flight reload's `git status` will see the
  user's change", which is exactly the reasoning this item disproves. It now says suppression means
  postpone, not discard.
- **Tests:** `GitBench.Tests/RepoWatcherDeferralTests.cs` — 8 methods pinning that an event arriving
  mid-read is deferred not dropped, survives ~6 debounce cycles, coalesces a burst into exactly one
  broadcast, defers each channel independently, does not re-fire after delivery, recovers from
  buffer overflow through the closed gate, and cancels cleanly on disposal. The last drives a real
  `FileSystemWatcher` over a temp dir so the arrival path itself is covered, not just the classifier.
  Shared fakes live in `GitBench.Tests/WatcherTestSupport.cs`.

### 4. Reconcile safety net — **DONE (2026-07-24)**

> Landed as designed below, restricted to the active repo per the *Scope per tick* bullet. The
> *Implemented* subsection at the end records the as-built shape.

Add a low-frequency revalidate: on window focus-gain, and every ~30s while the window is focused.
Any missed signal self-heals within one interval instead of persisting until the user switches
repos.

**Do not reuse `RepoRefreshRequestedMessage` for this.** It looks like the right channel — both
stores already subscribe (`RepoSnapshotStore.cs:121`, `RepoStatusStore.cs:105`) — but it means
*explicit user retry after a failed load*, and its handler nulls the local slice before reloading
(`RepoSnapshotStore.cs:204-215`) specifically so a byte-identical repeat error still re-renders.
A background tick on that channel would **blank the file list to a skeleton every 30 seconds**. It
is also active-repo-only (`:210`), so it cannot revalidate the warm set at all.

Broadcast the ordinary channel messages instead — `WorkingTreeChangedMessage` +
`RefsChangedMessage` for the repo(s) being reconciled. A reconcile tick *is* "assume the watcher
missed something on every channel", every subscriber already handles them idempotently, they carry
the warm-set fan-out the refresh message lacks (`RepoSnapshotStore.cs:163-202`), and speculative
broadcast of both is already house style — every dialog in `Features/` does exactly this after an
op. No new message type.

If a distinct channel is later wanted, the fix is to make the two intents distinguishable rather
than to add a second message: `RepoRefreshRequestedMessage(Guid RepoId, RefreshReason Reason)`,
where only `UserRetry` clears the slice. What must not happen is a third message whose handlers
drift apart from the first two — that is root cause A's shape.

**The window-focus signal is a genuine framework gap, confirmed.** `IWindow` exposes both
`IsFocused` and `OnFocusChanged` (`framework/ZGF.Desktop/IWindow.cs:15,23`) and `GuiApp` already
consumes them (`GuiApp.cs:131,147`) — but the window is not registered in the DI context. What
`GuiApp` registers at `:92-105` is `InputSystem`, `IContextMenuHost`, `IWindowCoordinates`,
`IPopupWindowFactory`, `ISecondaryWindowFactory`, `IUiDispatcher`, `IFrameTicker`, `SvgImageCache`,
`IClipboard`. Nothing app-side can observe focus.

The signal must be **app** focus, not main-window focus. GitBench has secondary windows (review,
diff) and popup windows; on macOS the menu popup is the key window, so main-window blur fires
whenever a context menu opens. A naive `MainWindow.OnFocusChanged` would reconcile on every menu
open and stop reconciling while the review window is in front. The right owner is the app object:
`IWindowedApp.Windows` (`framework/ZGF.Desktop/IWindowedApp.cs:6`) already holds every window —
main, popups, secondaries — and drops them as they close, so "is any of them focused" is the app's
own state to keep, not something the GUI layer should reassemble.

**Do not drive the 30s tick from `IFrameTicker`.** It is constructed as
`new FrameTicker(onActivated: app.MainWindow.RequestRedraw)` (`GuiApp.cs:70`), and `Add` invokes
that hook (`FrameTicker.cs:37`) — a permanently registered tick pins the render loop at full frame
rate forever. Use a `PeriodicTimer` in a hosted service, marshalling back through `IUiDispatcher`;
`UpdateService.RunAutoCheckLoopAsync` (`UpdateService.cs:119-137`) is the house pattern, including
re-reading the enable condition on the UI thread each tick rather than tearing the loop down.

- **Files:** new hosted service alongside `RepoWatcherService` (registered in `AppServices.cs`);
  `IsForeground` on `IWindowedApp` in `ZGF.Desktop`, surfaced as `IAppForeground` and registered
  in `GuiApp`.
- **Scope per tick:** the active repo unconditionally; the warm set is free (the channel messages
  already fan out to it) but N repos × 2 messages × a slow spindle is real cost — gate it behind
  §6's shared read gate, or reconcile only the active repo until §6 lands.
- **Watch:** with §3 in place a reconcile broadcast re-enters the same stores that open the activity
  gate, and §7's adaptive debounce keys off read latency. A 30s tick on a repo whose status takes
  10s must not stack; skip the tick if the previous reconcile's reads have not landed.
- **Acceptance:** no UI state can stay stale indefinitely, and an idle focused window performs no
  redraws between ticks. This is what structurally removes the "other weird de-sync issues I can't
  remember" category.
- **Note:** land with §3.

#### Implemented

- **`RepoReconcileService`** (`Features/Repos/`, hosted, registered next to `RepoWatcherService`).
  Takes its `TimeSpan interval` as a constructor parameter with `DefaultInterval = 30s` passed at
  the `AppServices` wiring site, so the app's cadence is visible where it is chosen and the tests
  can run the same loop at 120ms.
- **Scope is the active repo only**, as the *Scope per tick* bullet permits. Broadcasts
  `WorkingTreeChangedMessage` + `RefsChangedMessage` for it. Adding the warm set is §6's call.
- **`Reconcile` re-reads every condition on the UI thread** — activation, active repo, activity gate
  — rather than capturing them, so losing focus or switching repos changes the next tick without
  tearing the loop down. The no-stacking rule is `IRepoActivityTracker.IsActive(repo.Path)`: if the
  previous reconcile's reads have not landed, the tick is skipped rather than queued.
- **The initial `Subscribe` delivery is swallowed.** `State<T>.Subscribe` fires immediately with the
  current value; startup is the one moment every store has just loaded, so counting it as a focus
  gain would reconcile a repo read milliseconds ago.
- **Framework: the app owns the state.** `IWindowedApp` (`ZGF.Desktop`) gains
  `bool IsForeground` + `event Action<bool> OnForegroundChanged`, the same `IsFocused`/
  `OnFocusChanged` shape `IWindow` already uses. `OpenGlApp` / `MetalApp` maintain it with a shared
  internal `AppForegroundTracker` over the window list they already keep, watching each window's
  focus event as they create it. That covers popups and secondary windows for free, so a context
  menu (key window on macOS) or a review window taking focus does not read as backgrounding.
  - `ZGF.Gui.Desktop` adds `IAppForeground : IReadable<bool>` and an internal `AppForeground`
    projection over the app's property + event, registered by `GuiApp` beside `IUiDispatcher` /
    `IFrameTicker`. It holds no state of its own — it exists because the `Context` resolves by type
    and `IReadable<bool>` is not a usable key.
  - **`PointerOwnershipArbiter`, `GuiApp.HandleMainFocusChanged` and `SecondaryWindowFactory` are
    untouched.** An earlier cut fed activation from a new `AppFocusChanged` event on the arbiter,
    which meant teaching a class documented as "single source of truth for which window owns the
    pointer" a second, unrelated job, and threading a handle through both window factories. The
    app already knows its own windows; the arbiter's participant list was an accidental proxy for
    them.
- **Tests:** `GitBench.Tests/RepoReconcileServiceTests.cs` — 8 methods over a real `RepoRegistry`
  and real throwaway repos: focus-gain reconciles the active repo (asserting the repo id on both
  channels), the initial activation value does not, a focused window ticks repeatedly, an unfocused
  one never does, a tick is skipped while git is still reading and resumes once it goes idle, no
  active repo is silent, and both losing focus and disposal stop the loop.

### 5. A mutation's result can never be dropped — **DONE (2026-07-24)**

> Rewritten 2026-07-24. The original item ("put a per-repo queue in front of stage / unstage /
> discard / commit") is already done — at the service layer, since before this document existed.
> See root cause C. Landed as designed below plus one strengthening the design text did not have —
> the revalidation moved *into* the runner. The *Implemented* subsection at the end records the
> as-built shape.

Mutations and loads have opposite staleness semantics, and `ViewModelBase` offers only the load one.
A newer load *should* supersede an older one: both compute the same fact, and the fresher answer
wins. A newer mutation supersedes nothing — the older one's git process already ran, and its
continuation is the sole carrier of its error message and its change broadcast. Give mutations a
runner that says so:

```csharp
/// Runs a mutation — work whose result must always be delivered. No generation lane: superseding a
/// mutation is meaningless (the git process already ran and the index already moved), and dropping
/// its continuation drops both its error and the broadcast that reconciles the optimistic list.
/// Guarded only by disposal.
protected void RunMutation<T>(MutationEffects effects, Func<T> work, Action<T> onResult)
    where T : IOutcome<T>
```

Disposal is currently expressed *as* a lane bump (`ViewModelBase.Dispose:170-173`), so this needs a
real `_disposed` flag checked in the posted continuation — a small honesty fix in its own right: the
lanes are then only about staleness, not about lifetime.

**"The continuation always runs" is not enough on its own.** It closes the *dropped* broadcast, but
not the *omitted* one — and the omitted one is what C2 actually documents: `StashSelected` and
`RunSubmoduleUpdate` chose not to broadcast on failure, and the whole reason the two files disagreed
is that broadcasting was a convention held in seven separate callbacks. So the obligation moves out
of the callback and into the runner's signature: `MutationEffects` is a required parameter naming the
channels the op may have moved, and `RunMutation` broadcasts it in a `finally` around `onResult`.
There is no factory that produces an empty one, so "mutate and tell nobody" stops being expressible.

Broadcasting on failure is not a concession, it is the correct reading: a batch that failed partway
still moved the index, a pre-commit hook can reformat the working tree and *then* reject the commit,
and the panel that fired the op is painting the optimistic result either way.

What each shape rules out:

| Was representable | Now |
| --- | --- |
| A `git add` that failed and reported nothing | the continuation always runs, so `OpError` is always set |
| A mutation that moved the index and broadcast nothing | the broadcast is the runner's, in a `finally`, from a value with no empty case |
| A mutation that broadcast on success only | same — `MutationEffects` is not outcome-aware |
| `_deferStoreReloadUntilWorkingTreeChange` outliving the mutation that set it | the setting op's own continuation always broadcasts, which clears it |
| A stage and a submodule update invalidating each other | no shared lane — there is nothing left to invalidate |
| A disposed panel taking the app's view of the repo down with it | `onResult` is skipped on disposal; the broadcast is not |
| A lane bump standing in for "the VM is gone" | disposal is a `_disposed` flag; lanes mean staleness only |

- **Files:** `ViewModelBase.cs` (the runner + `_disposed`); `LocalChangesViewModel.cs`
  (`RunIndexMutation:1067`, `MarkResolved:614`, `StashSelected:712`, `RunSubmoduleUpdate:654`);
  `DiffViewModel.cs` (`RunFileIndexOp:338`, `RunResolve:389`, `RunApplyPatch:640`).
- **Deletions this earns:** `_opGen` disappears — those four ops are its only users. `_commitGen`
  goes too: `Commit:770` already returns early on `CommitBusy`, so no second commit can start, and
  its lane's only other bumper is `Dispose`. And `DiffViewModel`'s three hand-rolled `Task.Run`
  blocks — each with the same `service` / `bus` / `dispatcher` / `repoId` capture preamble, and one
  with a comment explaining why it hand-rolls (`:637-639`) — collapse onto the shared runner. Three
  bespoke blocks and two lanes become one method.
- **Watch:** the base `Gen` lane must stay exactly as it is. Loads *should* supersede;
  `ReloadAmendStagedThenApply:949` depends on it. Only mutations move.
- **Watch:** `StashSelected` and `RunSubmoduleUpdate` still skip `WorkingTreeChangedMessage` on
  failure. That stops mattering for C2's wedge once nothing can drop the *setting* op's broadcast,
  but a failed stash still leaves the lists un-revalidated — broadcast unconditionally, as
  `RunIndexMutation` already does.
- **Acceptance:** no index mutation can complete without delivering exactly one result; a failed
  stage always surfaces its error; the optimistic hold cannot outlive the mutation that set it.
  Closes root causes C1 and C2.
- **Note:** independent of every other item.

#### Implemented

- **`MutationEffects`** (`Infrastructure/MutationEffects.cs`) — a bus plus the channels one op may
  have moved. Three named constructors, one per thing an op can do (`Index(bus, repoId, path)`,
  `WorkingTree(bus, repoId)`, `Commit(bus, repoId)`), and two additive modifiers (`AndRefs()`,
  `AndSubmodulesOf(primaryId)`). Exactly one primary channel always fires; the extras go first, so
  the working-tree broadcast — the one that releases `_deferStoreReloadUntilWorkingTreeChange` —
  lands last, after every other store has been told to reload.
- **`ViewModelBase.RunMutation<T>(effects, work, onResult)`** — no lane, `_disposed` only, and
  `try { onResult } finally { effects.Broadcast() }`. The broadcast fires **even when the VM is
  disposed**: the revalidation is owed to the stores, not to the panel that happened to start the op.
  A deviation from the plan text, which said "guarded only by disposal" for the whole continuation.
- **`_disposed` replaced the dispose-time lane bumps**, and `_lanes` went with them — `CreateLane` is
  now `new GenerationGuard()`. Every posted continuation (`RunBackground`, `TryRunBackground`,
  `RunMutation`) checks it. Nothing outside `ViewModelBase` relied on disposal bumping a lane.
- **Eight call sites moved.** `LocalChangesViewModel`: `RunIndexMutation`, `MarkResolved`,
  `StashSelected`, `RunSubmoduleUpdate`, **and `Commit`** — the plan listed four; `Commit` came along
  because deleting `_commitGen` left it with nothing to run on. `DiffViewModel`: `RunFileIndexOp`,
  `RunResolve`, `RunApplyPatch`. Every `onResult` collapsed to `Update(s => s with { OpError =
  outcome.FailureMessage })` (plus `RunApplyPatch`'s render rollback and `Commit`'s editor clear),
  because `IOutcome<T>.FailureMessage` already existed and the success/failure fork was only there to
  decide whether to broadcast.
- **Deleted:** `_opGen`, `_commitGen`, `ViewModelBase._lanes`, `DiffViewModel`'s three hand-rolled
  `Task.Run` blocks with their `service` / `bus` / `dispatcher` / `repoId` capture preambles, and the
  comment at `DiffViewModel:637-639` explaining why one of them hand-rolled.
- **Behaviour deltas, all deliberate:**
  - A failed stash, a failed submodule update and a failed commit now revalidate. Previously silent.
  - `RunSubmoduleUpdate` now broadcasts `WorkingTreeChangedMessage` as well as
    `SubmodulesChangedMessage` — a submodule checkout changes the parent's `git status`, and it is
    what clears the optimistic hold.
  - A rejected commit broadcasts `CommitCreatedMessage`. Every subscriber treats it as
    "revalidate" (`RepoSnapshotStore`, `RepoStatusStore`, `DetachedHeadBannerViewModel`,
    `OperationViewModel` all just reload), and a hook that rewrote files before failing needs exactly
    that. The alternative was an outcome-aware `MutationEffects`, which reintroduces the empty case.
  - `DiffViewModel.RunFileIndexOp` now broadcasts with `IndexOnly: true`. It stages/unstages a whole
    file, which is precisely what the flag documents; it disagreed with `LocalChangesViewModel`'s own
    `RunIndexMutation` before. Net effect: the working-tree review stops refetching every loaded
    file's HEAD→disk diff on a file-level stage from the diff header.
- **`MarkResolved` keeps attempting every path after one fails** (the resolutions are independent) and
  reports the first failure — preserved from the `RunBackground<bool>` version it replaced.
- **Not moved, and why:** `BranchesViewModel`'s `_branchOpGen` / `_stashGen` and `CommitsViewModel`'s
  `_resetGen` / `_moveGen` / `_applyGen` are mutation lanes too, but each is gated to one in-flight op
  by state (`IsBranchOpInFlight`, `TryRunOutcome`), so no op can supersede another and none of them
  paints an optimistic list. With disposal no longer expressed as a bump they deliver unconditionally
  already. Worth revisiting only if one of them grows a second concurrent op.
- **Tests:** `GitBench.Tests/MutationRunnerTests.cs` — 11 methods over the real `ViewModelBase`, a
  real `MessageBus` and a drain-on-demand dispatcher. The headline one starts a slow mutation, lets a
  newer one land, then releases the first and asserts it still delivers (the C1 defect, inverted into
  an assertion). The rest pin: a failure delivers its error *and* broadcasts, a thrown exception folds
  into the outcome and still broadcasts, a *throwing continuation* cannot swallow the broadcast,
  disposal drops the continuation but not the broadcast, the index/working-tree/commit channels carry
  what they claim, extras broadcast before the working tree, a rejected commit still broadcasts — and,
  guarding the other side of the split, that a newer *load* still supersedes an older one.

**Rejected alternative — a per-repo mutation queue in the view model** (the original §5). It would
re-implement, one layer up, a lock that already exists one layer down: the same waits in the same
order, plus a second place for the "is this repo busy" question to be answered differently. Worse,
it addresses the symptom the re-grounding disproved (`index.lock`) and leaves the one that is real
(dropped results) untouched. The seam is right where it is; the defect is the lane above it.

**Rejected alternative — an outcome-aware `MutationEffects`** (`Broadcast(bool succeeded)`, with a
success-only channel set). It would let a commit broadcast `CommitCreatedMessage` only when a commit
was really created. But it puts the empty channel set back in reach — a mutation whose always-set is
empty is exactly the silent-on-failure shape this item removes — and it buys a naming nicety at the
price of the one guarantee the type exists for. `CommitCreatedMessage` is a reload trigger to all four
of its subscribers; a rare redundant reload after a rejected commit is cheaper than a representable
hole.

**Rejected alternative — make the hold a pending-mutation counter** instead of a bool, incremented
on the optimistic move and decremented in each continuation. Equivalent once §5 lands, and worse
before it: the hunk path sets the hold from `HunkAppliedOptimisticMessage` (`:184`) but is completed
by `DiffViewModel`'s continuation in a different view model, so the increment and decrement would
straddle a message boundary. The bool is fine; what was broken is that its clearing path could be
dropped.

### 6. One shared read gate — **DONE (2026-07-24)**

> Landed as designed below. The *Implemented* subsection at the end of this item records the as-built
> shape and the two small deviations.

Root cause E's second half: three background read dispatchers, each with its own idea of how many
git processes may run at once, none aware of the others. On a single spindle their sum is what
thrashes the disk — the cost is seek travel between repos' `.git` trees, not throughput, so 6+
concurrent `git status` walks are *slower* than a handful run in a bounded queue. This item puts one
shared gate in front of the background reads and sizes it once, deliberately.

#### The three caps as they actually stand

Re-read against current code — §2 and §11 moved every line the sketch cited. What each cap really
is, what it guards, and its one acquire site:

| Cap | Where | Size | Guards | Acquire / release / dispose |
| --- | --- | --- | --- | --- |
| `RepoStatusStore._gate` | const `RepoStatusStore.cs:65`, field `:79` | 6 | the all-repos `GetStatusSummary` probe — its *only* consumer | acquired `Refresh:231` (`await _gate.WaitAsync()`), released `:235` (`finally`), disposed `:270` |
| `StartupSweepCoordinator._throttle` | const `:29`, field `:32` | 4 | worktree + submodule *discovery* reads (`ListWorktrees` / `ListSubmodules`) — **not** just at startup: every `WorktreesChangedMessage` / `RefsChangedMessage` re-runs `ScheduleSync` → `RunThrottled` | acquired `RunThrottled:53`, released `:56`, disposed `:73` |
| `RepoSnapshotStore` fan-out | the read is `Task.Run` at `LoadSlice:376` and `WarmSlice:349`; **no cap at all** | ∞ | the heavy slices — `Load` (commit graph, libgit2), `GetBranches`, `GetLocalChanges` — for the active repo and up to four warm repos | none; every `Reload*` / `Warm*` just spawns a task |

Corrections to the citations the sketch and root cause E carried: `RepoStatusStore.cs:54` is now
`:65`/`:79`/`:231` (§2 inserted the `IRepoStatusIngest` interface, `RefreshUnlessActive`, `Reserve`,
`Publish`); `RepoSnapshotStore.cs:158-160` is now the *cached soft-refresh serve*, and the three
parallel loads are the `ReloadCommits` / `ReloadBranches` / `ReloadLocal` calls at `:161-163`, each
of which reaches the uncapped `Task.Run` at `LoadSlice:376` (`WarmSlice:349` for the warm set). Root
cause E's `StartupSweepCoordinator.cs:29` still points at the `4`. These are corrected in root cause
E below.

Two facts fall out of the re-read that the sketch did not have:

- **`Load` (the commit graph) does not go through `GitProcessRunner`** — it opens libgit2 directly
  (`GitService.cs:80`, `new Repository(repo.Path)`). It is also the dominant first-load cost (memory
  `startup-first-load-cost`). So the gate cannot live inside the runner: it would miss the single
  most expensive read and would have to special-case it anyway.
- **`StartupSweepCoordinator._throttle` is only half of that class.** `RunInitialSweep` /
  `MarkActiveReady` (`:36-71`) are a *deferral barrier* — hold the all-repos sweeps until the active
  repo's first load lands — not a concurrency cap. The sketch's "likely generalizes into the shared
  gate" is half-right: the throttle generalizes, the barrier must survive. Deleting the barrier would
  reopen exactly the regression it exists for (a many-repo startup contending with the one repo the
  user is waiting for). So §6 is **not** "one gate replaces the coordinator" — it is "the gate takes
  over the throttle; the coordinator keeps the barrier and becomes a client of the gate."

#### What the gate covers, and what it must not

The population, swept from `IGitService` and every background dispatch site:

| Tier | Reads | Behind the gate? |
| --- | --- | --- |
| Background loads | `Load` (commits, libgit2), `GetBranches`, `GetLocalChanges` (active + warm, via `RepoSnapshotStore`); `GetStatusSummary` (all-repos probe, via `RepoStatusStore`); `ListWorktrees` / `ListSubmodules` (discovery, via the sweep services); `GetMergeMessage` + the `ListSubmodules` inside `BuildLocalData`, which ride the local read's permit already | **yes** — these are the bursty, all-repos, self-scheduling reads that sum to the thrash |
| User navigation | `LoadDetails`, `GetDiff`, `GetFileText`, `LoadReviewStack`, `LoadRangeFiles`, `MergeBase`, `ResolveAutoReviewBase`, `PreviewMerge` / `PreviewRebase`, `IsAncestor` | **no, in §6's initial scope** — one-off, user-paced, latency-sensitive; gating them behind a background burst would regress the diff pane. Foldable later behind a priority tier (see *Fairness*) |
| Mutations / network | every `RunOperation` / `RunSimple` / `RunMergeLike` / `RunRemote*` / `RunLocked` op (stage, commit, checkout, merge, rebase, reset, stash, branch/tag ops, `Push`, `Fetch`, `Pull`, `DeleteRemoteBranch`, worktree/submodule mutations, `Clone`) | **never** — they take `GitRepoLocks`, and queueing a user-initiated write behind a background sweep is the opposite of the goal |

**Deadlock-freedom against §11's `GitRepoLocks` is by construction, and worth stating.** §11 gates
*writes per repo family* (`GitResource.LocalState` / `Remote`); §6 throttles *reads across repos*.
They compose because the two sets never cross: a gated read never calls `_locks.Acquire` — reads
take no mutation lock at all (`GitService` reads never touch `_locks`; confirmed by sweep) — and a
mutation never calls the gate — mutations dispatch through the view models straight into
`GitService`, never through the stores' read paths. The one read that runs *inside* a mutation's lock
acquisition, `ReadCommonGitDir` (`GitService.cs:53`, called by `GitRepoLocks.CommonGitDirKey`), is a
direct `_runner.Run` and stays ungated — gating it is both out of scope and unnecessary, since
nothing it could wait on holds a mutation lock. One gates writes-per-repo, the other throttles
reads-across-repos; neither is reachable from inside the other.

Ambiguous cases, resolved: `GetAmendStagedFiles` and `GetHeadCommitMessage` (root cause G / §10) are
reads currently on the UI thread; once §10 moves the amend read to the existing
`ReloadAmendStagedThenApply` async path it should take a gate permit like any background read (§10's
Discard/Stash sites stop reading at all — they project the store snapshot — so there is nothing to
gate there). The preview reads (`PreviewMerge` / `PreviewRebase`) are read-shaped but part of a
mutation flow and are user-initiated; leave them ungated with the rest of the navigation tier.

#### The gate's shape

A DI singleton the three background dispatchers share. It has to (a) bound concurrent reads, (b)
run the read off the UI thread and release the permit *before* the result is marshalled back — the
exact `WaitAsync … finally Release … then dispatcher.Post` shape all three sites already have — and
(c) record per-repo read timing so §7 can consume it without re-plumbing. Timing is attributed by a
read *kind* so §7's adaptive debounce can read the *status* read's duration specifically rather than
whatever read landed last:

```csharp
internal enum GitReadKind { Status, Commits, Branches, Discovery }

/// The single throttle for background git *reads*. Bounds how many run at once across all repos so a
/// many-repo tree can't seek-thrash one spindle, and times each read per (repo, kind) so §7's
/// adaptive debounce can scale a repo's watcher debounce toward its own status-read cost. Reads only:
/// mutations and network ops serialize on GitRepoLocks (§11) and never enter here — the two mechanisms
/// are disjoint by construction, so they cannot deadlock.
internal interface IGitReadGate
{
    /// Waits until fewer than MaxConcurrentReads are in flight, then returns a permit. Dispose it the
    /// instant the git read returns — it records the read's duration against (repoId, kind) and frees
    /// the slot. Dispose BEFORE marshalling results to the UI, so a permit is never held across the post.
    Task<Permit> Acquire(Guid repoId, GitReadKind kind);

    /// §7 seam: the most recent Status read's wall-clock for repoId, or null if none has completed.
    /// A Derived/observable is unnecessary — §7 polls it when it arms a debounce, not reactively.
    TimeSpan? LastStatusReadDuration(Guid repoId);

    readonly struct Permit : IDisposable { /* stopwatch + repoId + kind + release */ }
}
```

The migration is minimal because the interface mirrors the code that exists. `RepoStatusStore.Refresh`:

```csharp
_ = Task.Run(async () =>
{
    GitStatusSummary? summary;
    using (await _gate.Acquire(repoId, GitReadKind.Status))
    {
        try { summary = _git.GetStatusSummary(repo); } catch { summary = null; }
    } // permit released + timed here, before the post — same point _gate.Release() sits today
    dispatcher.Post(() => { /* unchanged: epoch check, root-cause-D keep-last-on-null */ });
});
```

`RepoSnapshotStore.LoadSlice` / `WarmSlice` take a `GitReadKind` alongside the `work` delegate and
wrap the `work(repo)` call the same way; `StartupSweepCoordinator.RunThrottled` gains a `Guid repoId`
and its body *becomes* `await using _gate.Acquire(repoId, GitReadKind.Discovery)` in place of its own
`_throttle`.

What each shape rules out:

| Was representable | Now |
| --- | --- |
| Three dispatchers each free to fire its own quota, summing to 6+4+∞ concurrent walks on one disk | one `MaxConcurrentReads` counts every background read across every repo |
| A permit held across the UI marshal, so a slow dispatcher pins a slot for a `dispatcher.Post` round-trip | `Permit` is disposed inside the read block, before the post — the seam names it |
| §7 re-deriving per-repo read timing from scratch (a second stopwatch in the watcher) | the gate already times every read it admits; `LastStatusReadDuration` is the seam |
| A mutation queued behind a background read sweep | mutations never call `Acquire`; the gate's only callers are the three read dispatchers |
| The startup barrier deleted along with the throttle it was tangled with | the barrier (`RunInitialSweep`/`MarkActiveReady`) stays on the coordinator; only `_throttle` moves |

#### Sizing and fairness

**Size it to the active repo's own fan-out, which is 3 — not 2.** A repo switch fires three read
tasks at once (`ReloadCommits` + `ReloadBranches` + `ReloadLocal`, `RepoSnapshotStore.cs:161-163`),
of which two are expensive (the libgit2 commit walk and the `git status`) and one is cheap
(`for-each-ref`). At size 2 the third task waits behind an expensive one — so on a slow spindle the
sidebar's branch list lands a full status-read *after* the commit graph and file list, a visible lag
on every switch. Size 3 lets the active repo's fan-out run unimpeded while still cutting cross-repo
concurrency from 6+ to 3. That is the principled number: **exactly the active repo's fan-out, and
nothing else** — while the active repo is loading, its three reads hold all three permits and every
background read (warm refresh, all-repos probe, discovery) queues behind it. The sketch's "~2" is one
too few; it would make the active repo wait on itself.

**Fixed constant, not disk-adaptive, not a preference.** On an SSD a cap of 3 costs nothing: reads
are ~50ms, the queue is empty, and the active fan-out is 3 anyway. Disk-type detection is unreliable
(network shares, external HDDs, fusion drives all lie), and a "max concurrent git reads" preference
is a knob no user can reason about. Fixed and low is the house answer, consistent with §11's fixed
per-repo semaphores.

**Starvation of the active repo is already ruled out, so priority is not required.** The concern is a
burst of all-repos background probes indefinitely delaying the active repo's file-list read — the one
the user is looking at. Three existing mechanisms already prevent it, without a priority tier:

- **Startup:** the coordinator's barrier holds the all-repos sweep until the active repo's first load
  lands (`MarkActiveReady`), so at startup the active fan-out runs *before* any background read is
  even released. The barrier is priority-by-ordering, and it survives §6.
- **Steady state:** §2's active-repo skip means the active repo generates *fewer* background reads
  (its working-tree/commit ticks ingest rather than probe), and §4's reconcile is active-only and
  skips while a read is in flight. The only unbounded-looking source, the warm set, is capped at four
  repos (`WarmRepoCount`, `RepoSnapshotStore.cs:80`). So the worst case is a switch into a repo while
  ≤2 warm refreshes hold permits: with size 3 the active repo still gets ≥1 permit immediately and
  the rest as the warm reads finish — bounded, never indefinite.

A two-tier gate (active fan-out unthrottled, background capped at 1) would be *strictly* more
responsive under load, but it adds a second ordering mechanism fighting the barrier and buys nothing
the barrier + bounded-warm + active-skip don't already cover. Rejected for now; revisit only if
measurement shows switch latency under background load.

**Within-repo vs cross-repo permits.** The thrash is seek travel between *different* repos' `.git`
trees; two reads on the *same* repo touch spatially close files and thrash far less. A flat global
cap of 3 does not distinguish them — but it does not need to, because the only case where one repo
wants ≥3 concurrent reads is the active fan-out, which the size-3 cap admits in full. A per-repo
sub-pool (allow the active fan-out, cap cross-repo at 1) is the same rejected two-tier refinement
seen from the other side.

#### Files

- **New `GitBench/Git/GitReadGate.cs`** — `IGitReadGate`, `GitReadGate`, `GitReadKind`, `Permit`.
  Instance `SemaphoreSlim(MaxConcurrentReads = 3)`, a `ConcurrentDictionary<Guid, TimeSpan>` of last
  Status-read durations, `Acquire` and `LastStatusReadDuration`. Instance-scoped (a DI singleton), the
  same reasoning §11 used to drop `GitRepoLocks`' statics. Disposes the semaphore.
- **`AppServices.cs`** — `context.AddSingleton<IGitReadGate, GitReadGate>();` beside the coordinator
  (`:59`). Injected into `RepoStatusStore`, `RepoSnapshotStore`, and `StartupSweepCoordinator`; the
  first two already resolve reflectively / via factory (`:117-125`), and the coordinator's plain
  `AddSingleton` (`:59`) resolves its new ctor param from the registered gate with no factory needed.
- **`RepoStatusStore.cs`** — delete `MaxConcurrentProbes` (`:65`), `_gate` (`:79`), its
  `WaitAsync`/`Release` (`:231`/`:235`) and `Dispose` (`:270`); inject `IGitReadGate`; wrap the read
  in `Refresh` in `Acquire(repoId, GitReadKind.Status)`. Nothing else in the store changes — the epoch
  ordering, the root-cause-D keep-last-on-null, and the ingest path are untouched.
- **`RepoSnapshotStore.cs`** — inject `IGitReadGate`; give `LoadSlice` (`:365`) and `WarmSlice`
  (`:345`) a `GitReadKind` parameter and wrap their `work(repo)` (`:379` / `:352`) in a permit;
  `ReloadCommits` / `WarmCommits` pass `Commits`, `ReloadBranches` / `WarmBranches` pass `Branches`,
  `ReloadLocal` / `WarmLocal` pass `Status`. The `ListSubmodules` + `GetMergeMessage` inside
  `BuildLocalData` ride the local read's single permit — one permit per slice task, not per git
  invocation.
- **`StartupSweepCoordinator.cs`** — delete `MaxConcurrentSweeps` (`:29`) and `_throttle` (`:32`);
  inject `IGitReadGate`; `RunThrottled` gains a `Guid repoId` and delegates to
  `Acquire(repoId, GitReadKind.Discovery)`; `Dispose` (`:73`) stops disposing `_throttle`.
  `RunInitialSweep` / `MarkActiveReady` are untouched — the barrier stays.
- **`WorktreeSyncService.cs:105`** and **`SubmoduleSyncService.cs:98`** — pass the `primaryId` /
  `hostId` they already hold into `RunThrottled`.
- **§7 hand-off:** `IGitReadGate.LastStatusReadDuration(Guid)` is the per-repo timing source §7's
  *Files* note asks for. §7 injects the gate into `RepoWatcher` and reads it when arming the debounce;
  no timing plumbing is added there.

#### Deletions this earns

- `RepoStatusStore.MaxConcurrentProbes` + `_gate` + its three touch points — gone; the probe read now
  shares the one gate.
- `StartupSweepCoordinator.MaxConcurrentSweeps` + `_throttle` + its `Dispose` — gone; `RunThrottled`
  is a two-line delegation. The class keeps only its deferral barrier, which is what it should always
  have been.
- `RepoSnapshotStore`'s uncapped fan-out stops being uncapped — no member deleted, but the "∞" row of
  the caps table disappears.

Three independent caps (6, 4, ∞) collapse to one number in one file.

#### What it does *not* close

Checked, because the dependency notes imply §6 might: **§6 makes §2's *Known gap* and *Residual*
cheaper, but closes neither.**

- **§2's Known gap (warm double-read):** a warm repo whose files change still runs *both*
  `WarmLocal` (file-list read) *and* the all-repos `GetStatusSummary` probe — `RefreshUnlessActive`
  skips only the *active* repo, and the warm set is invisible to `RepoStatusStore`. The gate now
  serializes the two so they cannot run concurrently, which bounds the cost to one queued read; it
  does **not** remove the duplication. Closing it needs either warm-set knowledge inside
  `RepoStatusStore` or §2's rejected reservation-keyed skip (whose correctness rests on
  `AppServices` subscription order, `:117` before `:125`). Now that the gate makes the duplicate cost
  one queued read rather than a second concurrent walk, the value of closing it drops — leave it as a
  follow-up, and do not smuggle the ordering-dependent skip in under §6.
- **§2's Residual (reconcile refs probe):** a §4 reconcile tick broadcasts working-tree then refs;
  the working-tree half is skipped + ingested, the refs half still probes even though the file-list
  read it rode already carried fresh ahead/behind. The gate throttles that probe; it does not
  suppress it. Same closure requirement, same conclusion — ~30s cadence on the active repo only, not
  worth the coupling.

#### Watch

- **Size the gate to 3, and treat 2 as a regression.** At 2 the active repo's branch list lags its
  own commit graph and file list by a full status read on every switch. A test must pin that the
  active fan-out is never self-throttled (see below).
- **Do not fold the barrier into the gate.** `RunInitialSweep` / `MarkActiveReady` must remain; a
  gate that also tried to be the startup barrier would break the `startup-first-load-cost` ordering.
- **Release the permit before the UI post.** Same invariant as today's `finally { _gate.Release() }`
  sitting before `dispatcher.Post`; holding a permit across the marshal would let a slow UI thread pin
  a slot. The `Permit`'s dispose point is inside the read block, not the enclosing `using var`.
- **`Load` is a libgit2 read, gated at the store, not the runner.** It counts against the same cap as
  the shell-out reads — correct, it is the expensive one — but it means the gate must wrap the
  `LoadSlice` work delegate, never live inside `GitProcessRunner`.
- **§7 shares this knob's timing.** `LastStatusReadDuration` reflects only `GitReadKind.Status` reads;
  if §7 later wants commit-walk timing it is a second accessor, not a re-plumb.

#### Acceptance

- No more than `MaxConcurrentReads` (3) background git reads run concurrently across all repos, at any
  disk speed.
- A mutation or network op is never delayed by the gate — staging during a background all-repos sweep
  runs at `GitRepoLocks` speed, not behind the sweep.
- The active repo's three-slice switch fan-out is admitted in full (never self-throttled).
- Per-repo status-read timing is recorded and readable through `LastStatusReadDuration` (the §7 seam).
- Disposal drains cleanly: no read is left holding a permit, and the semaphore disposes without a
  pending waiter throwing.
- Closes root cause E, second half (the uncoordinated caps). The first half — the duplicated
  working-tree traversal — was already closed by §2.

#### Test plan

Real throwaway repos where the reads are cheap; the concurrency assertions use a blocking fake read
so timing is deterministic, in the house style of `GitRepoLocksTests` / `RepoStatusStoreTriggerTests`.

- **`GitReadGateTests`** (new) — drive `GitReadGate` directly with a fake `read` that blocks on a
  gate the test controls:
  - the gate admits exactly `MaxConcurrentReads` reads and the `(N+1)`th blocks until one releases;
  - a permit released (disposed) frees exactly one waiter, FIFO;
  - `LastStatusReadDuration(repoId)` is null before any read, and after a timed fake read reflects its
    duration; a `Commits`/`Branches`/`Discovery` read does **not** move the Status timing;
  - disposal completes with a waiter pending without throwing, and no permit is leaked (a subsequent
    `Acquire` on a fresh gate still admits `MaxConcurrentReads`).
- **`RepoSnapshotStoreTests` (or an integration test over the real store + gate)** — a switch into a
  repo issues three slice reads that all acquire without any one blocking on another (the active
  fan-out is never self-throttled at size 3); assert by counting max-in-flight through a decorating
  gate.
- **Mutation-is-never-gated** — with the gate saturated (three fake reads parked), a real `Stage`
  through `GitService` still completes promptly. This is the acceptance criterion expressed directly:
  the read gate and `GitRepoLocks` are disjoint.
- **`RepoStatusStoreTriggerTests`** — extend the existing counting-decorator setup so a saturated gate
  defers a probe rather than dropping it, and the probe still lands (and still respects the epoch
  ordering) once a permit frees.

#### Rejected alternatives

**Put the gate inside `GitProcessRunner.Run`.** It looks like the one chokepoint every git process
passes through — but `Load` (the expensive commit walk) bypasses it via libgit2, mutations pass
through it and must *not* be gated, and `ReadCommonGitDir` runs through it *inside* a mutation's lock
acquisition. A runner-level gate would miss the costliest read, gate the writes it must leave alone,
and risk coupling the read throttle to the mutation lock. The gate belongs at the background read
*dispatch* sites, which are exactly the three stores/services this item touches.

**Merge `StartupSweepCoordinator` wholesale into the gate** (the sketch's first reading). Its throttle
is the gate; its barrier is not. Collapsing both into one type either loses the startup ordering or
teaches the gate a second, unrelated job. The coordinator becomes a thin client of the gate for the
throttle and keeps the barrier — one responsibility each.

**A two-tier / priority gate** (active fan-out unthrottled, background at 1). Strictly more responsive
under background load, but the barrier already gives the active repo startup priority, the bounded
warm set and active-repo skip bound steady-state contention, and a second ordering mechanism fights
the barrier. Premature; revisit only against a measured switch-latency regression.

**Close §2's warm double-read here via the reservation-keyed skip.** §6 *could* adopt §2's rejected
"skip if an ingest is already reserved" design and close the Known gap for the warm set. It stays
rejected: its correctness depends on `AppServices` subscription order with nothing at either site
saying so, and once the gate makes the duplicate cost one queued read the payoff no longer justifies
that coupling. Keep the skip active-only; leave the warm closure a separate, optional follow-up.

#### Implemented

The gate, the sizing (3), the three consumers' migration and the §7 timing seam landed as specified.

- **`GitBench/Git/GitReadGate.cs`** (new) — `GitReadKind { Status, Commits, Branches, Discovery }`,
  `IGitReadGate` (`Acquire` + `LastStatusReadDuration` + the nested `Permit`), and `GitReadGate` with
  an instance `SemaphoreSlim(3, 3)`, a `ConcurrentDictionary<Guid, TimeSpan>` of last Status-read
  durations, and `Dispose`. `MaxConcurrentReads` is `internal const` so the tests assert against it.
  Timing uses `Stopwatch.GetTimestamp()` at grant and `GetElapsedTime` on permit dispose. Mirrors
  `GitRepoLocks`' shape and doc style.
- **`RepoStatusStore.cs`** — `MaxConcurrentProbes` / the `SemaphoreSlim _gate` / its
  `WaitAsync`/`Release`/`Dispose` gone; `Refresh`'s read is wrapped in
  `using (await _gate.Acquire(repoId, GitReadKind.Status))`, closed *before* `dispatcher.Post`. Epoch
  ordering, root-cause-D keep-last-on-null and §2's ingest path untouched.
- **`RepoSnapshotStore.cs`** — `LoadSlice` and `WarmSlice` take a `GitReadKind`; each wraps `work(repo)`
  in a permit released before the post. Commits/Branches/Local map to `Commits`/`Branches`/`Status`;
  `BuildLocalData`'s inner `ListSubmodules` + `GetMergeMessage` ride the local read's single permit.
  §2 reserve/publish ordering unchanged.
- **`StartupSweepCoordinator.cs`** — `MaxConcurrentSweeps` / `_throttle` gone; `RunThrottled(Guid
  repoId, …)` delegates to `Acquire(repoId, GitReadKind.Discovery)`; the `RunInitialSweep` /
  `MarkActiveReady` deferral barrier is untouched. `WorktreeSyncService` / `SubmoduleSyncService` pass
  the `primaryId` / `hostId` they already hold.
- **`AppServices.cs`** — `AddSingleton<IGitReadGate, GitReadGate>()` beside the coordinator; injected
  into the three consumers (`RepoSnapshotStore` via its factory, the other two via plain resolution).
- **Deviations, both small.** (1) `Permit` is a `readonly struct` wrapping a single release `Action`
  closure that captures start/repoId/kind, rather than the four named fields the sketch listed — same
  observable behaviour, and it makes the decorating-gate test trivial. (2) `StartupSweepCoordinator`
  dropped `IDisposable` entirely: with `_throttle` gone it owns nothing to dispose, and it is a plain
  `AddSingleton` nobody disposed.
- **Left open as designed:** §2's Known gap (warm double-read) and Residual (reconcile refs probe) are
  serialized by the gate but not removed.
- **Tests:** `GitReadGateTests` (admits exactly `MaxConcurrentReads`, the N+1th blocks; one release
  wakes one FIFO waiter; `LastStatusReadDuration` null-before / reflects a Status read / is not moved
  by Commits/Branches/Discovery; disposal with a waiter pending does not throw and leaks no permit),
  `GitReadGateStoreTests` (the active switch fan-out all acquire concurrently at size 3 — measured by a
  barrier-based decorating gate that would underrun at size 2; a saturated gate never delays a real
  `Stage`, proving disjointness from `GitRepoLocks`), and a defer-not-drop case added to
  `RepoStatusStoreTriggerTests` (a saturated gate parks a probe, which lands epoch-guarded once a slot
  frees). Full suite: **447 passing**.

### 7. Adaptive watcher debounce — **DONE (2026-07-24)**

> Landed as designed below, including the knob split. The *Implemented* subsection at the end records
> the as-built shape and the one test-fixture deviation.

Scale a repo's watcher debounce toward the wall-clock its own `git status` costs, so a burst of
external events coalesces into roughly one reload per service-time instead of one reload per fixed
250ms. On a repo where status takes 4s, a 250ms window dispatches a reload the disk has no hope of
serving before the next window opens; a window sized near the read cost dispatches one and lets the
rest coalesce behind it.

> Re-grounded against the code on 2026-07-24, after §3 rewrote `RepoWatcher` and §6 built the timing
> seam. The sketch's two load-bearing citations both hold — `DebounceMs = 250` is exactly
> `RepoWatcher.cs:34` — but three things the sketch did not know are now decided against the real
> code: the four channels already share one debounce value (so "this repo's debounce" is one knob,
> not four), §3's `Drain` re-arms at that same value (so a naïve one-knob scale would also slow the
> deferral poll — **this item splits them**), and the gate §6 records is the last *single* reading
> with no smoothing of its own (so §7 must smooth, or one warm read collapses the window).

#### What §3 and §6 actually left

`RepoWatcher` today holds **one** `DebounceMs` constant (`RepoWatcher.cs:34`, value `250`, a
`private const int`), and there is one `RepoWatcher` per registry `Repo`
(`RepoWatcherService.StartWatching:72`). The four channels (`_workingTree` / `_refs` / `_worktrees`
/ `_submodules`) are four `Channel` objects that all read that one constant. So "scale *this repo's*
debounce" is a single per-watcher value — not per channel. Per-channel would be wrong anyway: all
four channels of a repo contend for the same spindle, so they should size to the same read cost.

The constant is read at exactly **two** sites, both inside `lock (_timerLock)` (`:59`):

| Site | Line | Role |
| --- | --- | --- |
| `Schedule` arms the debounce on arrival | `channel.Debounce.Change(DebounceMs, …)` `:317` | the **coalescing window** — how long to gather a burst before serving it |
| `Drain` re-arms while the gate is closed | `channel.Debounce.Change(DebounceMs, …)` `:331` | the **deferral poll** — how often to re-check whether our own git went quiet |

`_timerLock` guards `channel.Pending`, the `IsOurOwnWrite()` gate read (`:290,329`) and the
`Change()` together, so whatever value §7 computes for the arm must be read under that same lock to
stay consistent with it.

**These are two different quantities that happen to share a constant today.** The coalescing window
governs how eagerly a reload is *dispatched* while the disk is idle. The deferral poll governs only
how promptly a *deferred* drain notices the gate reopened — and a deferred drain never dispatches
anything (it re-arms and returns; the broadcast is unreachable while `IsOurOwnWrite()`). §3 collapsed
both onto `DebounceMs` because 250ms was a fine answer for each. Adaptation forces them apart.

#### One knob or split — split

The dependency note left for this item calls the coupling "the desired direction, but note the two
are one value now." Re-reading `Drain` (`:321-344`) against what each quantity controls, the coupling
is **wrong for the re-arm**, and this item splits them:

- **Scaling the coalescing window up is the whole point** — a 4s-read repo should gather ~4s of
  external churn before dispatching, because it cannot serve reloads faster than that.
- **Scaling the deferral poll up is a pure latency penalty with no upside.** While our own status
  read runs, the gate is closed and every re-armed `Drain` re-checks it. Lengthening that interval to
  4s means: after git actually goes quiet, a parked deferral waits up to 4s *more* before it notices
  and broadcasts — on the exact recovery path §3 built to never drop an event. It buys nothing,
  because a deferred drain cannot over-dispatch no matter how often it wakes (it broadcasts only when
  the gate is *open*, and the instant it does, the resulting read re-closes the gate). A short poll is
  a handful of lock-and-check timer callbacks per read; that cost is trivial and the latency win is
  real.

So `Schedule`'s arm uses the adaptive value; `Drain`'s re-arm stays at the fixed floor (250ms). The
"never queue faster than served" guarantee does not depend on the re-arm interval at all — it is the
activity gate that serialises reloads (a broadcast starts a read, the read closes the gate, the next
drain defers until it completes), and the adaptive coalescing window that keeps a single idle-time
burst from dispatching several. The re-arm poll is orthogonal to both.

#### The scaling function

The gate exposes the last **status** read's wall-clock, keyed by the same `Guid` the watcher already
holds as `_repo.Id`:

```csharp
TimeSpan? IGitReadGate.LastStatusReadDuration(Guid repoId);   // GitReadGate.cs:43
```

It returns null until the first `GitReadKind.Status` read on that repo completes (only Status reads
record — `GitReadGate.cs:82-83`, on `Permit.Dispose`, before the result is posted), and since §2 that
read is the file-list read that carries `--branch`, so its wall-clock includes the ahead/behind
compute — exactly the cost §7 wants to size against. It is a `ConcurrentDictionary` lookup with no
callback, so calling it under `_timerLock` cannot invert (lock order is `_timerLock` → the
dictionary's internal lock; nothing calls back into the watcher).

Three shape decisions, each grounded in what the acceptance criterion needs:

**Floor = 250ms** (today's `DebounceMs`). A fast repo keeps today's snappy coalescing; the window
never drops *below* it, so a repo whose reads are 30ms doesn't debounce at 30ms and fire a reload per
keystroke-flush.

**Ceiling = 2000ms.** A pathological 30s read must not set a 30s window that makes the app feel dead
to idle-time edits. Because the activity gate is the *hard* backstop against over-queueing, the
ceiling is a UX bound (max latency to reflect an edit made while the disk is idle), not a correctness
bound — capping the window below the read cost cannot reintroduce queueing, since a reload dispatched
at 2s starts a read that closes the gate and defers the next.

**Multiplier k = 1.** One reload costs one read to serve, so a window equal to the read cost coalesces
exactly one service-time's worth of events — matched to the drain rate by construction. `k < 1`
dispatches faster than served (the gate would catch it, but that defeats the point); `k > 1` adds
latency for no coalescing gain.

**Smoothing: fast-attack, slow-decay.** The gate stores only the *last* reading and overwrites it
each read (`GitReadGate.cs:83`), so used raw, one warm read that hit the OS page cache (80ms) would
collapse the window to the floor while the disk is still slow — re-opening the very eagerness §7
exists to remove. Symmetric smoothing fixes the collapse but also damps the response when the disk
*gets* slower, which is the direction the acceptance criterion cares about most. So react to a slower
read immediately and relax only gradually:

```csharp
// RepoWatcher.cs — constants beside DebounceMs, which is renamed DebounceFloorMs.
private const int DebounceFloorMs = 250;      // today's value; the window never drops below it
private const int DebounceCeilingMs = 2000;   // a pathological read must not make the app feel dead
private const double DecayAlpha = 0.25;       // how fast the window relaxes when reads speed up

// Guarded by _timerLock, like every value the arm reads. One RepoWatcher per repo, so one EWMA.
private double _smoothedReadMs;
private TimeSpan? _lastSample;                 // last value seen from the gate, to fold each read once

// Called under _timerLock. The coalescing window for the *next* arrival arm.
internal int CurrentDebounceMs()
{
    if (_readGate.LastStatusReadDuration(_repo.Id) is not { } reading)
        return DebounceFloorMs;                // cold repo: no read yet, fall back to today's default
    if (reading != _lastSample)                // a genuinely new read has landed since we last sampled
    {
        _lastSample = reading;
        var ms = reading.TotalMilliseconds;
        _smoothedReadMs = ms >= _smoothedReadMs
            ? ms                                             // attack: the disk got slower — react now
            : DecayAlpha * ms + (1 - DecayAlpha) * _smoothedReadMs;   // decay: relax slowly
    }
    return (int)Math.Clamp(_smoothedReadMs, DebounceFloorMs, DebounceCeilingMs);
}
```

The `reading != _lastSample` guard folds each read into the EWMA **once**, no matter how many events
arrive between two reads — otherwise a burst of arms during one read would over-weight that read's
value. The `k = 1` multiplier is implicit (the sample enters the EWMA as-is); a different `k` would
scale `ms` at the fold.

What each shape rules out:

| Was representable | Now |
| --- | --- |
| A window shorter than 250ms firing a reload per event-flush on a fast repo | `Math.Clamp(_, DebounceFloorMs, _)` |
| A 30s read setting a 30s window the user reads as a frozen app | `Math.Clamp(_, _, DebounceCeilingMs)` |
| One warm 80ms read collapsing a slow repo's window back to the floor | fast-attack/slow-decay EWMA; a single fast sample moves it by `DecayAlpha`, not to it |
| The disk getting slower but the window lagging behind for several reads | attack branch: `ms >= _smoothedReadMs` jumps immediately |
| A burst of arrivals between two reads folding the same reading N times | `reading != _lastSample` folds each read once |
| The deferral poll lengthening on a slow repo and delaying the recovery broadcast | `Drain` re-arms at `DebounceFloorMs`, not `CurrentDebounceMs()` (the split) |

#### Where the value is read

`Schedule:317` becomes `channel.Debounce.Change(CurrentDebounceMs(), Timeout.Infinite)` — still inside
`_timerLock`, so the smoothed state it mutates and the value it arms with are consistent. `Drain:331`'s
re-arm keeps `DebounceFloorMs` and its comment gains one line naming the split (the re-arm polls at
the fixed floor so a parked deferral notices the reopened gate promptly; only the arrival window
scales). Nothing else in `Drain` changes; `Pending`, the gate check and disposal are untouched.

- **Files:**
  - `RepoWatcher.cs` — rename `DebounceMs` → `DebounceFloorMs` (`:34`); add `DebounceCeilingMs`,
    `DecayAlpha`, the `_smoothedReadMs` / `_lastSample` fields, and `internal int CurrentDebounceMs()`;
    inject `IGitReadGate _readGate` into the constructor (`:62`) and store it; swap the arm at
    `Schedule:317` to `CurrentDebounceMs()`; leave `Drain:331` at `DebounceFloorMs` and extend its
    comment. `using GitBench.Git;` is already present (`:1`).
  - `RepoWatcherService.cs` — add `IGitReadGate readGate` to the constructor (`:20`), store it, pass
    it to `new RepoWatcher(...)` (`:72`). DI resolves it: `IGitReadGate` is the §6 singleton
    (`AppServices.cs:63`) and `RepoWatcherService` is registered reflectively
    (`AppServices.cs:138`, no factory), so **`AppServices.cs` needs no edit** — the new ctor param is
    satisfied automatically, like `RepoStatusStore`'s and `RepoSnapshotStore`'s gate params.
  - `GitBench.Tests/WatcherTestSupport.cs` — add a `FakeReadGate : IGitReadGate` (settable per-repo
    `TimeSpan?`, no-op `Acquire`).
  - `GitBench.Tests/RepoWatcherDeferralTests.cs:32` and `RepoWatcherClassifierTests.cs:29` — pass a
    `FakeReadGate` (default null) as the new argument. Mechanical; see *Watch* for why null keeps them
    green.
  - `GitBench.Tests/RepoWatcherDebounceTests.cs` (new) — direct `CurrentDebounceMs()` unit tests.
- **Deletions / simplifications this earns:** none — this adds a computed value, it removes nothing.
  It does *not* touch `RepoActivityTracker` (§3 already established the re-arm needs no quiet-notify
  hook), and it adds no message type, no channel, no reactive observable (the gate is polled at arm
  time — `GitReadGate.cs:39-43` was built precisely so §7 need not subscribe).
- **Watch:**
  - The constructor gains a parameter, so **every** `new RepoWatcher(...)` site must pass the gate:
    the one production site (`RepoWatcherService.cs:72`) and the two test sites above. This is a
    compile break, not a behaviour break — the build catches all three.
  - The existing deferral/classifier tests assume a fixed 250ms (`Pump.Settle = 900ms`, the 1600ms
    six-cycle drain in `A_deferred_signal_survives_many_debounce_cycles`, the 1200ms no-refire drain).
    They stay valid **only because** a `FakeReadGate` returning null makes `CurrentDebounceMs()` fall
    to the floor of 250 — arrival *and* re-arm both stay at 250 for those tests, so every timing
    assumption in `WatcherTestSupport.Pump` still holds. If the fake defaulted to a non-null duration
    those tests would need re-timing; it must default to null.
  - `CurrentDebounceMs()` mutates smoothed state and must be called under `_timerLock` in production.
    `lock` is reentrant, so a future caller that already holds the lock is safe; the direct unit tests
    drive it single-threaded, where no lock is contended.
- **Acceptance:** on a repo whose `LastStatusReadDuration` is large, the arrival debounce is
  measurably longer than on a repo whose reading is small or null; a fast repo never debounces below
  250ms; a pathological reading is capped at the ceiling; a single fast read after several slow ones
  does not collapse the window to the floor; and a deferred drain still re-checks the gate every
  ~250ms regardless of the repo's read cost. The end-to-end property — reload requests never queue
  faster than they can be served — is held by the activity gate serialising reloads (unchanged from
  §3), with the adaptive window ensuring a single idle-time burst dispatches once rather than every
  250ms.

#### Test plan

House style: direct `Channel`/method drives for the deterministic parts, a real `FileSystemWatcher`
over a temp dir only where the arrival path itself is under test (as `RepoWatcherDeferralTests`
already does). The seam the tests need is a fake timing source; `RepoWatcher`'s tests do **not**
currently fake the gate at all (they pass a `GateTracker`, which is the *activity* tracker — the 4th
ctor param), so §7 adds the fake:

```csharp
// WatcherTestSupport.cs — the timing source §7 injects. Default TimeSpan? is null, so every existing
// deferral/classifier test keeps its fixed-250ms behaviour unchanged.
internal sealed class FakeReadGate : IGitReadGate
{
    private readonly ConcurrentDictionary<Guid, TimeSpan> _durations = new();
    public void SetStatusDuration(Guid repoId, TimeSpan d) => _durations[repoId] = d;
    public TimeSpan? LastStatusReadDuration(Guid repoId)
        => _durations.TryGetValue(repoId, out var d) ? d : null;
    public Task<IGitReadGate.Permit> Acquire(Guid repoId, GitReadKind kind)
        => Task.FromResult(new IGitReadGate.Permit(() => { }));
}
```

`CurrentDebounceMs()` is `internal`, following §3's precedent (`ClassifyGitChange` and
`ScheduleAllChannels` were widened to `internal` for exactly this reason: the behaviour is
deterministic in isolation and driving it through real disk timing would test the OS, not the code).
`RepoWatcherDebounceTests.cs` drives it directly:

- **Null timing falls back to the floor.** Fresh `FakeReadGate` → `CurrentDebounceMs()` == 250.
- **A large reading scales the window up.** `SetStatusDuration(id, 1.5s)` → returns 1500, larger than
  a repo left at null (250).
- **The floor is respected.** `SetStatusDuration(id, 30ms)` → returns 250, never 30.
- **The ceiling caps a pathological reading.** `SetStatusDuration(id, 30s)` → returns 2000, never
  30000.
- **Slow-decay does not let one fast read collapse the window.** Feed 4s (→ 2000, at ceiling), then a
  single 100ms reading → still near the ceiling (`0.25·100 + 0.75·4000 = 3025` → clamp 2000), and
  emphatically not the 250 floor. A second and third 100ms reading walk it down gradually, proving the
  relaxation is slow rather than instant.
- **Fast-attack reacts immediately.** From a floor state, one 4s reading → 2000 on the very next call,
  not after several folds.
- **Each read folds once.** Set 4s, call `CurrentDebounceMs()` five times without changing the fake →
  the value does not drift across the repeated calls (the same reading is not folded five times).
- **The re-arm stays fixed (the split).** A behavioural check on the real watcher with a large fake
  reading: an event deferred behind a closed `GateTracker` still re-checks and broadcasts within one
  ~250ms cycle of the gate reopening, not within one ceiling-sized cycle — i.e. the deferral latency
  is independent of the repo's read cost. This is the one case that must go through the `Channel`
  timers (`GateTracker.Active` toggling as in `An_event_arriving_during_a_git_read_is_deferred`), so
  keep its window generous to stay non-flaky.

`GitReadGateTests` and `GitReadGateStoreTests` are unaffected — §7 does not touch the gate.

#### Rejected alternatives

**One knob (scale both the arm and the re-arm).** What the dependency note leaned toward. Rejected in
*One knob or split* above: it lengthens the deferral poll on exactly the slow repos where a parked
event is most likely, adding up-to-ceiling latency to §3's recovery path for no benefit, since a
deferred drain cannot over-dispatch however often it wakes.

**Raw last reading, no smoothing.** Simplest — read the gate, clamp, arm. Rejected because the gate
stores only the last reading (`GitReadGate.cs:83`); a single warm-cache read collapses a genuinely
slow repo's window to the floor, which is precisely the eagerness this item removes. The acceptance
criterion's "one fast reading must not re-open the queue problem" rules it out directly.

**Symmetric EWMA.** Fixes the collapse but damps the *upward* response too, so a repo whose disk just
got slower keeps under-debouncing for several reads. Fast-attack/slow-decay is the same amount of code
and protects the direction the acceptance criterion actually cares about.

**Max over a sliding window.** Most conservative against under-debouncing, but overreacts *upward*: a
single one-off slow read (a `git gc`, a cold cache after a laptop resumes) pins the window at the
ceiling for the whole window length even after the disk is fast again. The slow-decay branch gives the
same protection without letting one outlier dominate.

**A preference / fixed per-disk-class debounce.** Rejected for the same reason §6 fixed
`MaxConcurrentReads` rather than exposing it: the read cost is already measured per repo and per
moment, so a static knob is strictly worse than the number the gate hands over for free — and it would
be one more setting to explain and get wrong.

#### Implemented

The split, the EWMA, and the timing-source seam landed as specified.

- **`RepoWatcher.cs`** — `DebounceMs` renamed `DebounceFloorMs`; `DebounceCeilingMs = 2000` and
  `DecayAlpha = 0.25` added; `_smoothedReadMs` / `_lastSample` fields (both under `_timerLock`);
  `internal int CurrentDebounceMs()` exactly as sketched (null → floor, `reading != _lastSample`
  folds once into a fast-attack/slow-decay EWMA, `Math.Clamp` to `[floor, ceiling]`). The constructor
  gained an `IGitReadGate _readGate`; `Schedule`'s arrival arm is `CurrentDebounceMs()`, `Drain`'s
  re-arm stays `DebounceFloorMs` with the split named in its comment.
- **`RepoWatcherService.cs`** — constructor gained `IGitReadGate readGate`, threaded into
  `new RepoWatcher(…)`. As predicted, **`AppServices.cs` needed no edit** — the service is registered
  reflectively (`AddHostedService<RepoWatcherService>()`) and the gate is the §6 singleton, so the new
  param resolves automatically.
- **Tests:** `FakeReadGate` (settable per-repo `TimeSpan?`, defaulting to null, no-op `Acquire`) added
  to `WatcherTestSupport.cs`; the two existing `new RepoWatcher(…)` test sites pass it (null default
  keeps every deferral/classifier timing assumption on the 250 floor, so none needed re-timing). New
  `RepoWatcherDebounceTests.cs` — the eight cases: null → 250, a large reading scales up, floor
  respected, ceiling caps, slow-decay does not collapse on one fast read, fast-attack jumps on the next
  call, each read folds once, and the split behavioural check on the real watcher (a deferred event
  broadcasts within ~one floor cycle of the gate reopening, not one ceiling cycle). Full suite:
  **455 passing.**
- **Deviations, both cosmetic:** the new test file needed `using GitBench.Git;` (`Repo` lives there),
  and the slow-decay case feeds *distinct* small readings (100/90/80/70ms) rather than a repeated
  100ms, because the `reading != _lastSample` guard folds each *value* once — which is exactly the
  each-read-folds-once invariant, so distinct values are the faithful way to demonstrate the gradual
  walk-down. No behavioural divergence from the design.

### 8. Opt-in `core.untrackedCache` + `core.fsmonitor`

Both are large wins on a slow spindle. Both write to the user's repo config, so this must be an
explicit per-repo or global setting, never silent.

- **Files:** `PreferencesStore.cs` / settings UI, `GitService.cs`.
- **Acceptance:** off by default; enabling it is a visible user choice.

### 9. `worktrees/` gets the whitelist `modules/` already has — **DONE (2026-07-24)**

> Landed as designed below, with the layout assumption in *Verify first* confirmed empirically.
> See the *Implemented* subsection at the end of this item.

Give the `worktrees/` branch of `ClassifyGitChange` the same per-file discrimination the `modules/`
branch below it already has, and route the two meanings to two channels:

```csharp
if (gitRelativePath.StartsWith("worktrees/", StringComparison.Ordinal))
{
    var afterWorktrees = gitRelativePath.Substring("worktrees/".Length);
    var nextSlash = afterWorktrees.IndexOf('/');
    if (nextSlash < 0)
    {
        // worktrees/<name> itself created / deleted — the SET changed.
        ScheduleWorktrees();
        return;
    }
    // Inside one worktree's gitdir: its refs moved. Worktrees share refs/heads with the
    // primary, so the primary's Refs channel is the correct carrier — WorktreeSyncService
    // already fans RefsChangedMessage(primary) out to every worktree child.
    var perWorktree = afterWorktrees.Substring(nextSlash + 1);
    if (perWorktree is "HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "REBASE_HEAD"
        || perWorktree.StartsWith("refs/", StringComparison.Ordinal))
    {
        ScheduleRefs();
    }
    // index / index.lock / logs / gitdir / commondir / locked — ignored, exactly as
    // modules/<name>/index is.
    return;
}
```

`WorktreesChangedMessage` then means exactly one thing — *the set of worktrees changed* — and the
whitelist is what enforces it, the same enforcement `modules/` already carries. Nothing else moves:
no new message type, no new channel, no registry lookup inside the watcher (which has no registry
and should not grow one — resolving name → repo id is `WorktreeSyncService`'s job and it already
does it).

| Was representable | Now |
| --- | --- |
| A per-worktree `index` stat-cache write meaning "the worktree set changed" | only `worktrees` / `worktrees/<name>` directory events reach that channel |
| A worktree's HEAD moving with no repo's Refs channel ever hearing about it | routed to the primary's Refs channel, which fans out to every worktree child |
| `WorktreesChangedMessage` carrying either of two unrelated facts | one fact, enforced at the single classification site |

- **Files:** `RepoWatcher.cs:179-184` only.
- **Fixes:** root cause F, both halves — the spurious rediscovery cascade (F1) and the invisible
  external worktree checkout (F2).
- **Also fixes:** the granularity mismatch flagged under root cause B, and it fixes it in the right
  place. Once a read's echo is filtered on path, the activity gate stops being load-bearing for echo
  suppression on this branch — which is the same conclusion §3's open question reaches for
  `.git/index`. **The gate should not be what suppresses read echoes; the classifier should.**
- **Verify first:** the whole item rests on linked worktrees keeping a per-worktree `index` and
  `logs/` under `.git/worktrees/<name>/`. That is the documented `gitrepository-layout`, but it was
  reasoned from the layout, not observed in this app — confirm by watching the directory during a
  `git status` in a worktree before relying on F1's mechanism. F2 is verifiable by reading alone
  (`:164` is a whole-path equality; `worktrees/<n>/HEAD` cannot match it).
- **Watch:** routing worktree HEAD moves to `ScheduleRefs()` means an in-app checkout in a worktree
  now produces a watcher `RefsChangedMessage(primary)` in addition to the one the view model already
  broadcasts. The 250ms debounce coalesces most of it; it is a duplicate reload at worst, and §3's
  deferral makes it strictly better-behaved than dropping it.
- **Acceptance:** a `git status` in a worktree triggers no `git worktree list` on the primary, and
  an external `git checkout` inside a worktree refreshes that worktree's branch list and commit
  graph.
- **Note:** land with §3. Same file, same method, and §3's `Channel` refactor renames the
  `Schedule*` methods this item calls — doing them in either order is fine, but doing them apart
  means touching `ClassifyGitChange` twice.

#### Implemented

- **Verified, not assumed.** On a real throwaway repo (`git init` → commit → `git worktree add`),
  `.git/worktrees/<name>/` contains exactly `HEAD`, `ORIG_HEAD`, `commondir`, `gitdir`, `index`,
  `logs/` (with `logs/HEAD`), and `refs/`. Running `git status` **inside the worktree** moved that
  `index`'s mtime while the primary's own `.git/index` was untouched. F1's mechanism is real: every
  working-tree tick in a worktree writes into the *primary's* `.git` tree, and the old
  `StartsWith("worktrees/")` branch turned each one into a full `git worktree list` rediscovery.
- **`WorktreeSyncService.OnRefsChanged` confirmed as the carrier** — it fans
  `RefsChangedMessage(primary)` out to every child via `GetWorktrees`, so routing a worktree HEAD
  move to the primary's Refs channel reaches that worktree's branch list and commit graph.
- **One extra branch beyond the sketch.** The sketch only covers `worktrees/`; the bare `worktrees`
  directory event needed its own branch above it (it was an `||` on the old `if`), mirroring how
  `modules` sits above `modules/`.
- **Tests:** `GitBench.Tests/RepoWatcherClassifierTests.cs` — 30 cases driving `ClassifyGitChange`
  directly. Per-worktree `HEAD` / `ORIG_HEAD` / `MERGE_HEAD` / `REBASE_HEAD` / `refs/…` route to the
  refs channel and *not* the worktrees channel; `index`, `index.lock`, `logs/HEAD`, `gitdir`,
  `commondir`, `locked` produce nothing at all; only `worktrees` and `worktrees/<name>` reach the
  set channel. The neighbouring branches (primary refs, `.git/index`, `modules/…`) are pinned too,
  so a future edit to this method cannot quietly move them.

**Rejected alternative — widen the activity gate to the repo family.** The obvious reading of the
mismatch is that `Begin(W.Path)` should also close the gate for W's primary and siblings, since
their watchers see W's writes. That is backwards. The gate's only correct job is suppressing our own
write echoes, and §3 establishes that dropping is the wrong tool for that regardless; widening the
key would multiply §3's drop rate by the size of the repo family — a primary with four worktrees and
three submodules would have its gate closed by any of eight repos' git processes, on a machine where
each read costs seconds. The mismatch is a symptom of an incomplete path classifier, and the fix
belongs in the classifier.

**Rejected alternative — a `WorktreeRefsChangedMessage(primaryId, worktreeName)`.** Strictly more
precise: it would refresh only the worktree that actually moved, instead of every sibling.
But it adds a message type, a fifth watcher channel whose pending state is a *set of names* rather
than a bool (fighting §3's flag model), and a name→repo-id resolution step. The gain is avoiding a
few redundant refreshes among siblings that genuinely do share `refs/heads` — worth revisiting only
if a repo with many worktrees shows measurable cost.

### 10. No git on the UI thread — **DONE (2026-07-24)**

> Landed as designed below. The *Implemented* subsection at the end records the as-built shape and the
> three small deviations.
>
> Re-grounded against the code on 2026-07-24. Every citation below was re-read (§1/§5/§11 moved the VM
> layer and `GitService.cs`; §2 added `GitStatusSummary Summary` to `LocalChangesSnapshot`). **No
> premise turned out wrong.** Three things the original sketch did not have, now written down: the
> cold-store case (the store slice can be `null` or `Failed`, and the service call it replaces always
> returned *something*); the fact that Amend has *two* on-thread reads, not one, of which only the
> expensive one moves; and that both dialog VMs keep `IGitService` for their *mutation* (only the
> *read* is deleted).

Close the three sites in root cause G. Two of them are not "move the read off-thread" but "delete
the read": `DiscardChangesViewModel:45` and `StashDialogViewModel:53` re-run a full `git status`
(`GetLocalChanges`) for the active repo, whose `LocalChangesSnapshot` `IRepoSnapshotStore` is already
holding. Project it — the dialogs then open in a frame, and they show exactly the same lists the panel
behind them shows, which is the stronger property anyway (today they can disagree, because they are
two independent reads a `git status` apart).

Amend is the one site that still needs a git call — but only the *cheap* one stays on the thread.
`AmendSession.Begin` makes two reads (`AmendSession.cs:56-57`): `GetHeadCommitMessage` (a single
`git log -1 HEAD`, which seeds the editor's title/description) and `GetAmendStagedFiles` (an
index-vs-`HEAD^` diff, which populates the staged panel). The diff is the expensive one and no store
carries it, so defer it onto the async reload the VM already runs on every load
(`ReloadAmendStagedThenApply:926`); the head-message read is one object read whose result is editor
*text*, and it stays synchronous (see *The amend site* for why deferring it is a net loss).

| Was representable | Now |
| --- | --- |
| A `git status` inside a widget `Build` pass | the dialogs read a snapshot value they are handed |
| The Discard dialog listing different files than the panel that opened it | one snapshot, one reader |
| Blocking the frame that would have drawn the loading state | nothing status-class runs on the UI thread |
| An index-vs-`HEAD^` diff blocking the Amend tick | the diff is deferred to the existing async reload |

#### The two dialogs — project the snapshot

The dialog VMs currently take `IGitService` and, in their constructor, call `GetLocalChanges` and
build rows from the `Fetched<LocalChangesSnapshot>.Ok` case (`DiscardChangesViewModel:45`,
`StashDialogViewModel:53`). The constructor runs inside the widget `Build` pass
(`DiscardChangesDialog.Build:30`, `StashDialog.Build:27` construct the VM), i.e. inside layout — which
is why the freeze cannot even draw a spinner.

**What the store exposes.** `IRepoSnapshotStore.LocalChanges` is `IReadable<Fetched<LocalChangesData>?>`
(`RepoSnapshotStore.cs:32,93`), holding the *active* repo's slice (`_local`). The projection is one
expression:

```csharp
store.LocalChanges.Value is Fetched<LocalChangesData>.Ok ok
    ? ok.Value.Snapshot                 // a LocalChangesSnapshot
    : LocalChangesSnapshot.Empty(repo.Id);
```

**The snapshot carries everything both dialogs need, provably** — because it is the *same read*. The
store's `LoadLocalChanges:305` calls the identical `_git.GetLocalChanges(repo)` the dialogs call today
and keeps its `.Snapshot` unchanged (`BuildLocalData:308` only wraps it with drift + merge message).
So `Staged`, `Unstaged`, each `FileChange`'s `Path` / `OldPath` / `Status` are byte-for-byte what the
dialog reads now; the only thing projection loses is *point-in-time freshness*, and that loss is the
feature — the store's value is exactly what the panel behind the modal is showing.

- **Discard** reads `snapshot.Unstaged` (`BuildRows:121-128`). Carried.
- **Stash** reads `snapshot.Staged` + `snapshot.Unstaged` and derives untracked from
  `f.Status == FileChangeStatus.Added` (`BuildRows:153`, feeding `_untrackedPaths`, which
  `DoStash:132` reads to decide `--include-untracked`). `FileChange.Status` is in the snapshot, so
  the `--include-untracked` decision survives untouched.
- Since §2 the snapshot also carries `GitStatusSummary Summary`; neither dialog reads it, so it is
  free extra fidelity, not a new dependency.

Both VMs keep `IGitService` — they still call `DiscardChanges` / `CreateStash` from their command
(`DiscardChangesViewModel:111`, `StashDialogViewModel:135`). Only the *read* is deleted; the mutation
stays. (The current *Files* bullet's "take the snapshot instead of the service" is imprecise on this
point.)

**The cold-store gap.** `store.LocalChanges.Value` is not always `Ok`. It is `null` on a cross-repo
switch whose cache is empty (`OnActiveChanged:160-162` soft-serves cache-or-null) and after an
explicit retry (`OnRefreshRequested:217` nulls it), and it is `Fetched<LocalChangesData>.Failed` when
the load failed. The service call the dialog replaces always returned *something* (usually fresh, real
data); a projected slice can be absent or an error. Resolve it by degrading to
`LocalChangesSnapshot.Empty(repo.Id)` — which yields empty rows and the dialog's *existing* empty-state
placeholder (`LocalchangesDiscardDialogNoChanges` / `StashDialogNoChanges`). This is exactly what both
VMs already do on a non-`Ok` service result today (`DiscardChangesViewModel:45-47` falls to
`Array.Empty`, `StashDialogViewModel:53-55` to `LocalChangesSnapshot.Empty`), and it mirrors how the
panel behind the modal handles the same states (`ApplyLoadingState:869` / `ApplyLoadFailure:884` blank
the lists). So the cold case never blocks and never shows a spinner it cannot draw — it shows the same
"no changes" state the panel shows.

The degradation is narrower than it looks. **Discard is structurally unreachable when cold**: it is
launched from a populated panel row or from `DoDiscardAll` over `State.Value.Unstaged` — both require
the panel to have loaded, which means the store slice is `Ok`. Only **Stash** (from the toolbar) can be
opened before the active repo's slice has landed, and only then does the rare cold-open show empty
instead of a one-frame-late list. That is an honest, bounded regression against a synchronous read that
almost always succeeded — and the right trade for never freezing.

#### The amend site

`SetAmend(true)` (`LocalChangesViewModel:277`) calls `AmendSession.Begin` (`:284`) synchronously; both
of `Begin`'s git reads (`AmendSession.cs:56-57`) run on the UI thread. Trace of what each result feeds:

- `GetHeadCommitMessage` → `session.Title` / `session.Description` → the editor's title/description
  boxes (`SetAmend:290-291` copy them into state). This is **editor text**.
- `GetAmendStagedFiles` → `session.StagedFiles`, shown by the staged panel via
  `ComputeDisplayedStaged:1107` (returns `session.StagedFiles` while `Amending`). This is a **file
  list**.

**No store carries either.** The commit graph holds only the subject line (`CommitNode.Summary`,
`CommitGraph.cs:37`), not the body, so the head *message* is a genuine read; the index-vs-`HEAD^` diff
is a genuine read the store has no equivalent of.

**Defer the diff, keep the head read.** The staged list is safe to fill a beat late — it is exactly
what `ReloadAmendStagedThenApply:926` already does on every load: `RunBackground(GetAmendStagedFiles)`
(`:929`), then, guarded by *still the active repo* (`:932`) and *still `Amending`* (`:933`), it calls
`session.UpdateStagedFiles(...)` and re-applies. So the mechanism is:

1. In `SetAmend(true)`, read `GetHeadCommitMessage` synchronously (one object, no walk) and seed the
   session's `StagedFiles` with the current index-staged list `_stagedFromIndex` (`:1005`,
   `= snap.Staged`), which is already in VM state from the store snapshot the panel is showing.
2. Enter `Amending` synchronously (no `GetAmendStagedFiles` on the thread).
3. Kick the existing async staged refresh so the diff lands and replaces the seed.

The seed is index-vs-`HEAD` (`_stagedFromIndex`), a *narrower* list than the final index-vs-`HEAD^`
amend view (the last commit's own changes are absent until the diff lands) — a brief under-count, not a
wrong list, replaced the moment the async diff returns. The async apply already no-ops if the editor
left `Amending` (the `:933` guard) and is superseded by a newer load through the base `Gen` lane, so
toggling amend off mid-flight, or a store reload racing it, are both already handled.

**Why the head read stays synchronous.** `GetHeadCommitMessage` seeds editor *text*, and text cannot
be deferred cleanly: deferring it either flashes a blank editor for one round-trip, or — if the user
types into the just-cleared box before the message lands — the async fill clobbers what they typed.
Guarding that (fill only if the box still equals the seed) is real machinery bought for a `git log -1`
that reads one commit object with no working-tree, index, or tree walk — the same order of cost as
opening any commit in the graph, and orders of magnitude below the `git status` and `git diff --cached`
reads this item exists to remove. Keeping it synchronous preserves today's instant, correct editor fill
for free. The strict "zero calls" variant is a rejected alternative below.

**`AmendSession` sheds its git dependency.** With both dialog reads gone and the head read moved to the
VM, `AmendSession.Begin(IGitService, Repo, …)` becomes a value factory that takes the already-read head
message and the seed staged list and makes *no* git calls — a pure state holder. That is a small
deepening (the type no longer reaches for `IGitService`), and it is what makes the seed unit-testable.
`AmendSession` is used only from `LocalChangesViewModel` (`EditorMode.Amending`), so no other entry
point needs the same treatment.

#### Files

- **`AmendSession.cs`** — replace `Begin(IGitService, Repo, string, string)` (`:42-64`, the two git
  reads at `:56-57`) with a value factory taking the head `Title`/`Description` and the seed staged
  list; the type stops referencing `IGitService`. Keep `UpdateStagedFiles`, `Classify`.
- **`LocalChangesViewModel.cs`** — `SetAmend:277`: read `GetHeadCommitMessage` synchronously, build the
  session via the new factory seeding `StagedFiles` with `_stagedFromIndex`, enter `Amending`, then
  call the async staged refresh. Extract that refresh out of `ReloadAmendStagedThenApply:926` (a
  `RefreshAmendStaged(Repo)` that `RunBackground`s `GetAmendStagedFiles`, keeps the active + `Amending`
  guards, updates the session, and re-applies the displayed staged) so both entry and the load path
  share it.
- **`DiscardChangesViewModel.cs:45`** — constructor takes a `LocalChangesSnapshot` and builds rows from
  it unconditionally (`BuildRows(snapshot)`); drop the `GetLocalChanges` call. Keep `IGitService` for
  `DoDiscard:111`.
- **`DiscardChangesDialog.cs:30`** (`Build`) — resolve `ctx.Require<IRepoSnapshotStore>()`, project the
  active repo's snapshot (shared helper below), pass the value into the VM.
- **`StashDialogViewModel.cs:53`** — constructor takes a `LocalChangesSnapshot`; drop the
  `GetLocalChanges` call. Keep `IGitService` for `DoStash:135`.
- **`StashDialog.cs:27`** (`Build`) — resolve the store, project the snapshot, pass it in.
- **A shared projection helper** — one internal static (`Ok ? .Snapshot : Empty(repo.Id)`) both dialog
  `Build`s call, so the cold-store handling lives in one place and is tested once. `IRepoSnapshotStore`
  is already resolvable from `Context` (the panel VM takes it by ctor injection, and the dialogs
  already `ctx.Require<…>` other registered services).
- **`GitBench.Tests/AmendUnstageTests.cs`** — updates for the new `AmendSession` factory signature (it
  calls `AmendSession.Begin(_git, _repo, "", "")` at `:78,108`; those become explicit
  `_git.GetHeadCommitMessage` / `GetAmendStagedFiles` calls feeding the factory).
- **`GitBench.Tests/CountingGitService.cs`** — extend to count (or a sibling fake to throw on)
  `GetLocalChanges` / `GetHeadCommitMessage` / `GetAmendStagedFiles`; today it only counts
  `GetStatusSummary`.

#### Watch

- **Point-in-time, not reactive.** The dialogs read the snapshot once and do not subscribe to the
  store. A background reload landing while the modal is open must *not* reshuffle the list — that would
  invalidate the user's checkbox set and the pre-checked selection under their cursor. Deliberate; the
  reactive variant is rejected below.
- **Always the active repo.** Both openers pass `_registry.Active.Value` (`RequestDiscard:677`,
  `ActionsToolbarViewModel.DoStash:213` / `DoDiscardAll:224`), and the store slice is the active repo's
  — so the projected slice is always the right repo. A dialog opened for a non-active repo is not
  reachable, so there is no cross-repo mismatch to design against.
- **Untracked survives.** Stash's `--include-untracked` rides `FileChange.Status == FileChangeStatus.Added`
  in the `Unstaged` list (`StashDialogViewModel:153`), carried identically because the store ran the
  same `GetLocalChanges`.
- **Amend seed is an under-count, briefly.** `_stagedFromIndex` (index-vs-`HEAD`) shows a narrower list
  than the amend view (index-vs-`HEAD^`) until the async diff lands. Acceptable stale-while-revalidate,
  the same shape the panel already uses; the diff apply is guarded (`:933`) and lane-superseded.
- **Head read stays on the thread by design.** It is the one remaining UI-thread git call, and it is a
  single-object read with no walk. If a slow spindle ever shows a *measurable* amend-entry stall
  attributable to it, move it off with the unchanged-since-seed editor guard (rejected alt 1).

#### Acceptance

- Opening Discard or Stash issues **zero** `IGitService` calls on the UI thread (a counting/throwing
  fake records none at `Build`), and the dialog's list equals the store snapshot's projection.
- Ticking Amend issues **no `git status` and no `git diff` on the UI thread** — at most one
  single-object `GetHeadCommitMessage` — and the staged panel fills from the deferred async diff.
- On a cold or failed store slice, both dialogs open in a frame showing their empty state rather than
  blocking.
- Closes root cause G.

#### Test plan

Frame timing is only truly observable at runtime, so the writable tests assert the **call-count proxy**
and the **projection equality** — which together are what "opens in a frame" reduces to.

- **`DiscardChangesViewModelTests` / `StashDialogViewModelTests`** (VM-level, the core): construct the
  VM with a `LocalChangesSnapshot` value and a throwing-on-read `IGitService`; assert construction
  touches no read (the throw never fires) and the row list equals the snapshot's projection — Discard's
  `Unstaged` sorted; Stash's `Staged`+`Unstaged` merged with the untracked flag set exactly for
  `Status == Added`. A second case hands `LocalChangesSnapshot.Empty` and asserts empty rows + the
  empty-state header, pinning the cold-store branch.
- **Projection helper test**: drive the shared helper against a fake `IRepoSnapshotStore` whose
  `LocalChanges.Value` is each of `Ok` / `null` / `Failed`; assert `Ok` yields the wrapped snapshot and
  the other two yield `Empty(repo.Id)`.
- **Amend entry** (extend `AmendUnstageTests`' real-repo style): assert `SetAmend(true)` seeds
  `session.StagedFiles` from the current staged list synchronously and issues **no**
  `GetAmendStagedFiles` on the calling thread — using a drain-on-demand fake `IUiDispatcher` (the
  pattern `RepoStatusStoreTriggerTests` already uses) so the deferred diff runs only after an explicit
  drain — then drain and assert the staged list is replaced by the index-vs-`HEAD^` result. A counting
  `IGitService` confirms exactly one `GetHeadCommitMessage` and zero `GetAmendStagedFiles` before the
  drain.
- **`AmendSession` unit test**: the new value factory builds a session from a head message + seed list
  with no `IGitService` in scope at all (the type no longer takes one), pinning the shed dependency.
- **Optional, fuller**: a `GuiTestHarness` test that builds `StashDialog` / `DiscardChangesDialog` with
  a Context whose `IRepoSnapshotStore` is a fake and `IGitService` throws on read, opens the widget, and
  asserts the rendered row count matches the snapshot and no read fired. This is the closest to the
  real "no git in `Build`" property; the VM-level tests are the pragmatic floor.

#### Rejected alternatives

- **Move `GetHeadCommitMessage` off-thread too, for a literal zero-calls acceptance.** It buys the
  crisp "no `IGitService` on the UI thread, full stop" line — but the read seeds editor text, so it
  costs either a blank-editor flash for one round-trip or an unchanged-since-seed clobber guard, all to
  defer a single-object read that causes no freeze. Not worth the machinery; revisit only if a slow
  spindle ever shows a measured amend-entry stall.
- **Make the dialogs reactive** — subscribe to `IRepoSnapshotStore.LocalChanges` and update the list
  while the modal is open. Rejected: a modal is a point-in-time decision over a checkbox selection, and
  a list mutating under the user would invalidate both their checks and the pre-checked set. Freshness
  here is a bug, not a feature.
- **Keep the read but move it off-thread** — open the dialog with a spinner, load async. Rejected: it
  reintroduces the two-reads divergence (the dialog can still show a different list than the panel it
  came from), and it adds a loading state to a modal whose data the store is already holding. The point
  is not "read faster" but "the data is already here".
- **Pass the store into the VM** (VM reads `.Value` in its constructor) rather than a
  `LocalChangesSnapshot` value. Weaker: it couples the dialog VM to `IRepoSnapshotStore` and pushes the
  cold-store branch into the VM. Handing a plain snapshot value keeps the VM a pure function of its
  input and the projection (with its cold-store fallback) in one shared, single-tested place.

- **Note:** independent of everything else — the only open item touching no file any other item does.

#### Implemented

The two deletes and the amend defer-the-diff landed as designed.

- **`AmendSession.cs`** — `Begin` is now a pure value factory
  `Begin(preAmendTitle, preAmendDescription, HeadCommitMessage? head, IReadOnlyList<FileChange> seedStagedFiles)`
  making no git calls; the type no longer references `IGitService` or `Repo`. The null-`head` empty-text
  defense moved into the factory. `UpdateStagedFiles` / `Classify` untouched.
- **`DiscardChangesViewModel.cs` / `StashDialogViewModel.cs`** — constructors take a
  `LocalChangesSnapshot` value and build rows from it; the `GetLocalChanges` read is gone. Both keep
  `IGitService` for their mutation (`DoDiscard` / `DoStash`).
- **`LocalChangesProjection.cs`** (new) — `internal static class LocalChangesProjection` in
  `GitBench.Features.LocalChanges`; `ActiveSnapshot(store, repo)` is the one expression
  `Ok ? .Snapshot : Empty(repo.Id)`, the single home of the cold-store fallback. Chosen over a static on
  a dialog class for discoverability and direct unit-testability.
- **`DiscardChangesDialog.cs` / `StashDialog.cs`** — each `Build` resolves
  `ctx.Require<IRepoSnapshotStore>()`, projects via `ActiveSnapshot`, and passes the value in.
- **`LocalChangesViewModel.cs`** — `SetAmend(true)` reads `GetHeadCommitMessage` synchronously (the
  single-object editor-text seed, on-thread by design), builds the session seeding `StagedFiles` with
  `_stagedFromIndex`, enters `Amending`, then calls the extracted `RefreshAmendStaged(Repo)` — a
  `RunBackground` of `GetAmendStagedFiles` with the active-repo + still-`Amending` guards. Both amend
  entry and the reload path now share it, so `GetAmendStagedFiles` never runs on the UI thread.
- **Cold-store & amend-seed, as built:** the projection returns `Empty(repo.Id)` for `null`/`Failed`,
  flowing into the dialogs' existing empty-state; the amend seed is the panel's index-vs-`HEAD`
  `_stagedFromIndex`, replaced when the async index-vs-`HEAD^` diff lands under the existing guards and
  `Gen`-lane supersession.
- **Deviations, all minor:** (1) the projection helper is a dedicated static type rather than a static
  on a dialog class (doc left the home open); (2) the full-VM amend-entry test needs a registry +
  dispatcher + store fixture, so it lives in a new `LocalChangesAmendEntryTests` rather than inside
  `AmendUnstageTests` (whose `Begin` call-site updates were still made); (3) `ReloadAmendStagedThenApply`
  now applies the snapshot *then* refreshes the amend diff (stale-while-revalidate) so both paths share
  one `RefreshAmendStaged`, where it previously deferred the whole apply until the diff landed — §10's
  endorsed model, guards and lane-supersession intact.
- **Tests:** `DiscardChangesViewModelTests` / `StashDialogViewModelTests` (construct with a snapshot +
  a throwing-on-read `IGitService`; assert zero reads and the exact row projection, plus an empty-snapshot
  cold-store case); `LocalChangesProjectionTests` (`Ok`/`null`/`Failed` → snapshot / `Empty` / `Empty`);
  `LocalChangesAmendEntryTests` (real repo, drain-on-demand dispatcher + a gate on `GetAmendStagedFiles`:
  after `SetAmend` the staged list is the index-vs-`HEAD` seed, exactly one `GetHeadCommitMessage`, zero
  `GetAmendStagedFiles`; after release+drain it is replaced by the `HEAD^` diff); two `AmendSession`
  factory unit tests with no `IGitService` in scope; `CountingGitService` extended to count/throw on the
  three reads. Full suite: **465 passing.**

### 11. The mutation lock stops covering the network — **DONE (2026-07-24)**

> Landed as designed below. The *Implemented* subsection at the end records the as-built shape and
> the one deviation (the lock policy became its own type rather than more statics in `GitService`).

`Push` and `Fetch` do not touch the index, and they hold the per-repo mutation semaphore for a
network round-trip (root cause C3). Give network-only ops their own per-repo-family lock:
`Push:1877`, `Fetch:2021` and `DeleteRemoteBranch:2466` move off the index lock; everything else
stays exactly where it is.

`Pull` keeps the index lock — it checks the working tree out and takes git's own index lock anyway,
so queueing a stage behind it is correct, not a bug. Pull is then the only op holding two locks,
which means there is no second two-lock op to invert against and no ABBA hazard to design around.

Key each lock by the resource it protects rather than by the caller: the index lock by working-tree
path (today's key, correct — the index is per worktree), the remote lock by the common git dir, so a
primary and its linked worktrees share one. That is also the shape C4's ref-store gap wants, without
committing to a full ref/index split.

| Was representable | Now |
| --- | --- |
| A stage click waiting on a `fetch --all --recurse-submodules` | different locks; the stage runs at disk speed |
| A pool thread parked for a network round-trip on a `sem.Wait()` | waits are bounded by disk work again |
| A primary and its worktree pushing concurrently under two different locks | the remote lock is keyed by the shared git dir |

- **Files:** `GitService.cs:45-81` (a second semaphore map + the common-git-dir key), `:3033-3054`
  (`RunLocked` gains the lock choice), `Push:1877`, `Fetch:2021`, `DeleteRemoteBranch:2466`,
  `Pull:1978`.
- **Watch:** `RepoOperationsStore` already prevents same-type remote ops per repo id
  (`:107,119,137`), but its comment claims *"only same-type ops on one repo are serialized … matching
  git's own per-repo index lock"* (`:52-54`) — which has been false since the service lock landed and
  becomes false differently after this. Fix the comment with the change.
- **Watch:** resolving the common git dir costs a `git rev-parse --git-common-dir` per repo; memoize
  it per path, and fall back to the working-tree path when it fails so an unreadable repo degrades to
  today's behaviour.
- **Acceptance:** staging during a fetch runs immediately; staging during a pull still queues.
- **Note:** `GitService.cs` only. Independent of §5, though both are about the same seam.

#### Implemented

- **Deviation — the lock policy became a type.** `GitBench/Git/GitRepoLocks.cs` holds
  `GitResource { LocalState, Remote }`, the two semaphore maps, the common-git-dir memo, `KeyFor`
  and `Acquire`. The plan said "a second semaphore map + the common-git-dir key" in `GitService.cs`;
  that would have been five more statics and a three-branch key helper inside a 3,800-line file, with
  the whole rationale for the split living in a comment nobody reads at the call sites. As its own
  type the policy has one place to be documented and — the actual reason — one place to be *tested*:
  `KeyFor` is the observable form of "which paths contend", so the keying is assertable without
  timing a real fetch.
- **The maps stopped being `static`.** `GitService` is a DI singleton, so instance-scoped is
  equivalent, and it retires the "static and never trimmed" footnote under C4.
- **Moved to the remote lock:** `Push:1877`, `Fetch:2021`, `DeleteRemoteBranch:2466`, via two new
  entry points (`RunRemoteOperation`, `RunRemoteSimple`) that sit beside `RunOperation` / `RunSimple`.
  `RunLocked` gained a required `GitResource` parameter; the ~36 existing mutating call sites pass
  through `RunOperation` / `RunMergeLike` / `RunSimple` and did not change, and the four direct
  `RunLocked` callers name `GitResource.LocalState` explicitly.
- **`Pull` takes both, LocalState first** (`RunLocked(..., LocalState, …)` with an inner
  `_locks.Acquire(Remote, …)`). It is the only two-lock op, which is what makes the order unable to
  invert; the pre-existing parent→submodule nesting through `ReattachSubmodulesOnBranchTip` sits
  inside it and is now written down next to the enum.
- **`PublishBranch` deliberately stayed on the local-state lock.** It is a network push, so it has the
  same defect in miniature — but it also writes `branch.<x>.merge` into local config, and the doc
  scoped this item to the three ops whose queueing was the reported symptom. Worth a follow-up.
- **C4's ref-store gap is still open**, as designed: `DeleteBranch` in a primary and
  `CheckoutLocalBranch` in a linked worktree still take different `LocalState` locks and can still
  collide on git's own `packed-refs.lock`. The common-git-dir key is now available if that is ever
  observed; git degrades it to a failed op with a clear message, not corruption.
- **Tests:** `GitBench.Tests/GitRepoLocksTests.cs` — 11 methods, 12 cases. The two resources do not contend
  (the acceptance criterion, expressed directly); the same resource on the same repo does; different
  repos never do; a shared common git dir shares the remote lock while keeping distinct local ones; a
  relative `--git-common-dir` resolves against the working tree; null / empty / throwing resolvers all
  fall back to today's per-working-tree behaviour; the resolver runs once per working tree; trailing
  separators normalize. The last drives real git — `git worktree add`, then assert the primary and the
  worktree land on one remote key and two local ones.

**Rejected alternative — a full ref-lock / index-lock split**, with every op declaring which
resources it touches. Most mutating ops move refs *and* the index (commit, checkout, merge, rebase,
reset, cherry-pick, revert, stash), so nearly everything would take both, the ordering invariant
would become load-bearing across ~30 call sites, and the only ops that gain anything are the three
above. The narrow version buys the whole symptom fix and introduces one two-lock op instead of
thirty.

---

## Dependency notes for task breakout

- ~~§1 → §2: same seam, land together. §1 goes first.~~ Both done. §1's consumers and §2's single
  observation now close root cause A end to end.
- ~~§3 → §4, §3 → §9: same seam / same file.~~ All three done, landed together as planned.
- ~~§4 → §6/§7: §4 introduces the app's first periodic git read, shipped active-repo-only so §6 can
  route it through the shared gate.~~ §6 done — the reconcile read and both stores' reads now share
  one `IGitReadGate`.
- ~~§3 → §7: the adaptive debounce now has a re-arm loop to interact with.~~ §7 done — it **split** the
  one `DebounceMs` constant rather than sharing it: the arrival arm scales toward the read cost, the
  re-arm poll stays at the fixed floor, because a deferred drain never over-dispatches and lengthening
  its poll only delays §3's recovery broadcast. See §7's *One knob or split*.
- ~~§2 unblocks the measurement half of §6 and §7.~~ All landed: §2 made per-active-repo read timing
  meaningful (one read per tick), §6 records it (`IGitReadGate.LastStatusReadDuration`), and §7 now
  consumes it. That timing seam has no remaining open consumers.
- ~~§5, §10 and §11 are independent of everything else and of each other, and can go in any order.~~
  All three done. §5 + §11 were the two halves of root cause C; §10 closed root cause G. §10 is the
  last symptom-closing item, so **only §8 (opt-in git config, independent, lowest priority) remains.**
- ~~§4 partially nets §5~~ — moot now that nothing can drop a mutation's broadcast. The reconcile
  tick is still the safety net for missed *watcher* events; it is no longer covering for the mutation
  path.
- §8 is independent and lowest priority.

**Still open from §3:** the gate-removal question. §3 established that the read-side gate is
redundant for every branch of `ClassifyGitChange` *except* a possible `git gc --auto` rewriting
`packed-refs`, and that was never verified empirically. Deferring rather than dropping is safe
either way, so this stays an optional follow-up — but §9 strengthened the case: the classifier, not
the gate, is now what suppresses read echoes on the `worktrees/` branch too.

**Symptom → item mapping**, for prioritising:

| Reported symptom | Closed by | State |
| --- | --- | --- |
| Branches shows a pull, pull button greyed out | §1 + §2 | done |
| Files in Unstaged that are not there | §3 (+ §5 for the mutation-failure variant) | done |
| File panel frozen after a stage + another op, until something else touches the tree | §5 | done |
| A stage/unstage that silently did nothing | §5 | done |
| Whole UI freezes opening Discard / Stash, or ticking Amend | §10 | done |
| A click during a fetch does nothing until the fetch ends | §11 | done |
| Unremembered / intermittent de-sync | §4 | done |
| General slowness on HDD | §1, §2, §6, §7, §10, §11 (§9 if worktrees are in use; §8 optional) | all but §8 done |
| A worktree's branch list / graph stale after an external checkout | §9 | done |

**Only §8 remains.** Opt-in `core.untrackedCache` + `core.fsmonitor` — both large wins on a slow
spindle, both writing to the user's repo config, so they must be an explicit, visible per-repo or
global setting, never silent. It is independent of every implemented item, closes no reported symptom
on its own (it accelerates the reads the other items coordinate), and is lowest priority. It is the
only item still at sketch standard; grounding it means reading `PreferencesStore.cs` / the settings UI
and `GitService.cs`'s config-write surface, and deciding the opt-in granularity (per-repo vs global)
and where the toggle lives. See §8.

**Three follow-ups deliberately left open, none blocking §8:**

- §2's *Known gap* — the warm set still runs `WarmLocal` + the all-repos probe. §6's gate bounds it to
  one queued read; closing it needs warm-set knowledge in `RepoStatusStore` and, once throttled, the
  payoff no longer justifies the coupling.
- §2's *Residual* — a reconcile tick still probes refs after ingesting the working-tree half. Same
  gate-throttled, same closure requirement.
- The §1/§2 upstream-glyph mismatch (below): a branch tracking another *local* branch reports
  `HasUpstream: true` to the status store while `ParseUpstream` maps it to `None`, so its HEAD glyph
  dims while its badge shows `0/0`.

Root cause E's `GitService.cs` and `RepoSnapshotStore.cs` line citations and root cause D's
`RepoStatusStore.cs` citation were corrected during §2's re-grounding (drifted by four and two lines);
no claim in either changed.

Root cause E's `GitService.cs` and `RepoSnapshotStore.cs` line citations and root cause D's
`RepoStatusStore.cs` citation were corrected during §2's re-grounding (drifted by four and two lines);
no claim in either changed.

One follow-up surfaced by §2's verification and deliberately not folded in: a branch whose upstream is
another *local* branch (`branch.X.remote = .`) reports `HasUpstream: true` to the status store while
§1's `ParseUpstream` maps it to `None`, so its HEAD glyph dims while its badge shows `0/0`. Pre-existing
since §1; §2 changed only which read fills the slot, not what the fields mean.
