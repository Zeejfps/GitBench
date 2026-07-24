# UI coherence on slow disks

> On a 5400 RPM HDD the app visibly de-syncs: the Branches view shows a pending pull while the
> pull button is greyed out, files linger in Unstaged after they are gone, and everything is
> sluggish. None of these are HDD-specific bugs — they are mostly latent races whose windows are
> normally sub-100ms on an SSD and become seconds-wide when every `git status` costs seconds. One
> (A) is not a race at all: it is the same number stored in four places on mismatched refresh
> triggers, and it does not self-correct at any disk speed. This document records the root causes
> found by reading the sync path end to end, and the work items that close them.
>
> **Status: §1, §3, §4 and §9 are implemented (2026-07-24). §2, §5, §6, §7, §8, §10 and §11 are
> analysis only.** Items are ordered by what fixes the reported symptoms first; §1+§2 are one seam
> and should land together, as should §3+§4+§9. §9, §10 and §11 are appended rather than inserted
> in priority order to avoid renumbering.
>
> **Root cause C was re-grounded against the code on 2026-07-24 and its premise was wrong.**
> Index mutations *are* serialized per repo — `GitService` has held a per-repo mutation semaphore
> all along (`GitService.cs:38-81`), and every mutating method goes through it. C has been rewritten
> around what is actually broken at that seam, §5 rewritten to match, and §10 / §11 added for the
> two defects the re-grounding surfaced.
>
> **Next up: §2** (the other half of §1's seam — one status read feeding both the file lists and
> the ahead/behind/dirty summary), then §5 (mutation results being discarded, independent of
> everything) or §6 (the shared read gate, which §4 now has a second consumer waiting on).

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

### C. Mutations *are* serialized per repo — the lane above them throws their results away

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

`RepoStatusStore.cs:190` keeps the last known summary when a probe fails. Deliberate, and the
comment explains why, but combined with (A) a transient status failure can pin the toolbar to old
numbers indefinitely while the branches list moves on.

### E. Duplicated work and uncoordinated disk concurrency

Every working-tree tick on the active repo runs **two full working-tree traversals**:

- `status --porcelain=v2 -z --untracked-files=all` for the file lists (`GitService.cs:1061`)
- `status --porcelain=v2 --branch --untracked-files=normal` for the summary (`GitService.cs:1088`)

Same walk, twice, in two processes. On a cold 5400 RPM drive this is the dominant cost, and it is
pure duplication: `--branch` on the first call yields everything the second produces.

There is also no global disk gate. Three independent caps exist and none knows about the others:

- `RepoStatusStore` semaphore of 6 (`RepoStatusStore.cs:54`)
- `StartupSweepCoordinator` semaphore of 4 (`StartupSweepCoordinator.cs:29`)
- `RepoSnapshotStore` fires three parallel `Task.Run` with no cap (`RepoSnapshotStore.cs:160-162`)

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

### G. Three git reads run on the UI thread

Found while sweeping C's call sites: every `IGitService` call in the app is dispatched through
`Task.Run` / `AsyncCommand` / `RunBackground` — **except three**, which run synchronously on the UI
thread.

| Site | Call | Triggered by |
| --- | --- | --- |
| `AmendSession.Begin:56-57` | `GetHeadCommitMessage` + `GetAmendStagedFiles` | ticking the Amend checkbox (`LocalChangesViewModel.SetAmend:287`) |
| `DiscardChangesViewModel:45` | `GetLocalChanges` — a full `git status` | `DiscardChangesDialog.Build:30` |
| `StashDialogViewModel:53` | `GetLocalChanges` — a full `git status` | `StashDialog.Build:27` |

The last two run **inside a widget `Build` pass**, i.e. inside layout. On a repo where `git status`
costs seconds, opening Discard or Stash freezes every window for that long — and it cannot show a
spinner or a skeleton, because the frame that would draw one is the frame that is blocked.

Both dialogs are also re-reading data the app already holds: they are always constructed for the
active repo (`RequestDiscard:690` reads `_registry.Active.Value`), whose `LocalChangesSnapshot` is
already in `IRepoSnapshotStore`. This is not "move the read off-thread" so much as "stop doing the
read". Amend's `GetAmendStagedFiles` is a diff against HEAD~1 that the store genuinely doesn't
carry — but the VM already refreshes it asynchronously on every load
(`ReloadAmendStagedThenApply:949`), so the machinery to populate it a beat later exists.

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

### 2. One observation, one timestamp

§1 unifies the *consumers*; this unifies the *observation*, which is what stops the same disease
recurring between `RepoStatus.IsDirty` (toolbar Stash, `ActionsToolbarViewModel.cs:78`; repo-bar
dot, `RepoNodeViewModel.cs:123`) and the file lists those signals are supposed to summarise.

Add `--branch` to the file-list status call and return branch / ahead / behind / dirty alongside
the file lists from that single read, as **fields of one record** — `LocalChangesSnapshot` gains a
`GitStatusSummary Summary`, so there is no constructor that would let the lists and the summary
come from two invocations. The cheap all-repos probe stays for *non-active* repos, where file
lists are not loaded.

Verified `--branch` composes with `-z --untracked-files=all`, and the headers come back
NUL-terminated like every other record:

```
# branch.oid c84118f…\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +0 -0\01 .M … \0
```

So `ParseStatusPorcelainV2`'s existing NUL walk already yields the headers; only
`ParseStatusRecord:949` needs a `'#'` case. Factor the per-header parsing out of
`ParseStatusSummary:1100` so the `-z` and `\n` paths share it.

`RepoStatusStore` keeps sole ownership of its per-repo slot and gains a second *input*: an ingest
entry point that `RepoSnapshotStore.LoadSlice` calls when a local-changes load lands, writing
under the existing `_epoch` guard. One slot, ordered — not a second slot.

- **Files:** `GitService.cs` (`RunGitStatusPorcelain:1055`, `ParseStatusRecord:949`,
  `ParseStatusSummary:1100`), `LocalChanges.cs` (`LocalChangesSnapshot`), `IGitService.cs`,
  `RepoSnapshotStore.cs` (`LoadSlice`), `RepoStatusStore.cs`.
- **Also fixes:** halves per-tick disk work for the active repo (root cause E, first half), once
  the now-redundant probe is skipped for the active repo on `WorkingTreeChangedMessage` /
  `CommitCreatedMessage` (both already reload local unconditionally). Keep the probe on
  `RefsChangedMessage` — a fetch moves ahead/behind without touching the working tree.
- ~~**Also fixes:** the missing trigger from root cause A — probe on active-repo change, so a switch
  never leaves the toolbar showing a probe from before the switch.~~ **Done — landed with §1**
  (`RepoStatusStore.Start` subscribes to `IRepoRegistry.Active`); see §1's *Implemented* notes.
- **Watch:** ingest on a *load* result only, never on the cached value `OnActiveChanged:158`
  serves, or a switch-back writes a stale summary over a fresher slot.
- **Watch:** the dirty bool is currently "any non-header record"; that stays correct only once
  `'#'` is handled explicitly rather than falling through.
- **Known gap:** warm repos still double up (both stores reload on `WorkingTreeChangedMessage`).
  Fixing that needs warm-set knowledge in the status store; leave it to §6 rather than leak it.
- **Acceptance:** the active repo's file lists and its ahead/behind/dirty signals can never
  originate from two different git invocations.
- **Note:** land with §1 — same seam. §1 is independently shippable and closes the reported
  symptom on its own; §2 depends on nothing in §1 but is much less useful without it.

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

### 5. A mutation's result can never be dropped

> Rewritten 2026-07-24. The original item ("put a per-repo queue in front of stage / unstage /
> discard / commit") is already done — at the service layer, since before this document existed.
> See root cause C.

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
protected void RunMutation<T>(Func<T> work, Action<T> onResult) where T : IOutcome<T>
```

Disposal is currently expressed *as* a lane bump (`ViewModelBase.Dispose:170-173`), so this needs a
real `_disposed` flag checked in the posted continuation — a small honesty fix in its own right: the
lanes are then only about staleness, not about lifetime.

What each shape rules out:

| Was representable | Now |
| --- | --- |
| A `git add` that failed and reported nothing | the continuation always runs, so `OpError` is always set |
| A mutation that moved the index and broadcast nothing | the broadcast is in the continuation, which always runs |
| `_deferStoreReloadUntilWorkingTreeChange` outliving the mutation that set it | the setting op's own continuation always broadcasts, which clears it |
| A stage and a submodule update invalidating each other | no shared lane — there is nothing left to invalidate |

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

**Rejected alternative — a per-repo mutation queue in the view model** (the original §5). It would
re-implement, one layer up, a lock that already exists one layer down: the same waits in the same
order, plus a second place for the "is this repo busy" question to be answered differently. Worse,
it addresses the symptom the re-grounding disproved (`index.lock`) and leaves the one that is real
(dropped results) untouched. The seam is right where it is; the defect is the lane above it.

**Rejected alternative — make the hold a pending-mutation counter** instead of a bool, incremented
on the optimistic move and decremented in each continuation. Equivalent once §5 lands, and worse
before it: the hunk path sets the hold from `HunkAppliedOptimisticMessage` (`:184`) but is completed
by `DiffViewModel`'s continuation in a different view model, so the increment and decrement would
straddle a message boundary. The bool is fine; what was broken is that its clearing path could be
dropped.

### 6. One shared read gate

Replace the three independent concurrency caps with a single shared gate sized ~2 for git
*reads*. Keep mutations and network operations outside it (they are user-initiated and must not
queue behind background sweeps).

- **Files:** `StartupSweepCoordinator.cs` (likely generalizes into the shared gate),
  `RepoStatusStore.cs`, `RepoSnapshotStore.cs`.
- **Acceptance:** a many-repo tree cannot thrash a single spindle. Closes root cause E,
  second half.

### 7. Adaptive watcher debounce

Measure how long the last status read took for a repo and scale that repo's 250ms debounce
(`RepoWatcher.cs:34`) toward it. On a repo where status takes 4s, coalescing 4s of events costs
nothing and prevents queueing reloads faster than the disk can serve them.

- **Files:** `RepoWatcher.cs`, plus a per-repo timing source (the shared gate from §6 is a
  natural place to record it).
- **Acceptance:** reload requests never queue faster than they can be served.

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

### 10. No git on the UI thread

Close the three sites in root cause G. Two of them are not "move the read off-thread" but "delete
the read": `DiscardChangesViewModel` and `StashDialogViewModel` re-run a full `git status` for the
active repo, whose `LocalChangesSnapshot` `IRepoSnapshotStore` is already holding. Project it — the
dialogs then open in a frame, and they show exactly the same lists the panel behind them shows,
which is the stronger property anyway (today they can disagree, because they are two reads).

Amend is the one that genuinely needs a git call: `GetAmendStagedFiles` diffs the index against
HEAD~1 and no store carries it. Enter amend mode synchronously with the current staged list and let
the existing `ReloadAmendStagedThenApply:949` path replace it — that machinery already runs on every
load, so this is a matter of not blocking on it once at entry.

| Was representable | Now |
| --- | --- |
| A `git status` inside a widget `Build` pass | the dialogs read a snapshot they are handed |
| The Discard dialog listing different files than the panel that opened it | one snapshot, one reader |
| Blocking the frame that would have drawn the loading state | nothing on the UI thread blocks |

- **Files:** `DiscardChangesViewModel.cs:45`, `StashDialogViewModel.cs:53` (take the snapshot
  instead of the service — the dialog widgets already resolve from `Context`, so `DiscardChangesDialog.Build:30` /
  `StashDialog.Build:27` pass it in), `AmendSession.cs:42-64` + `LocalChangesViewModel.SetAmend:280`.
- **Watch:** the stash dialog needs untracked status to decide `--include-untracked`
  (`StashDialogViewModel:131-133`); `FileChange.Status == Added` is in the store's snapshot, so this
  survives the switch.
- **Watch:** entering amend asynchronously must not clobber text the user typed in the gap. They
  just enabled amend, so the editor is being replaced wholesale either way — but the apply should
  still no-op if the editor left `Amending` in the meantime, the same guard
  `ReloadAmendStagedThenApply` already carries.
- **Acceptance:** no `IGitService` call on the UI thread; opening Discard / Stash and ticking Amend
  are frame-time operations on a cold 5400 RPM repo. Closes root cause G.
- **Note:** independent of everything else.

### 11. The mutation lock stops covering the network

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

**Rejected alternative — a full ref-lock / index-lock split**, with every op declaring which
resources it touches. Most mutating ops move refs *and* the index (commit, checkout, merge, rebase,
reset, cherry-pick, revert, stash), so nearly everything would take both, the ordering invariant
would become load-bearing across ~30 call sites, and the only ops that gain anything are the three
above. The narrow version buys the whole symptom fix and introduces one two-lock op instead of
thirty.

---

## Dependency notes for task breakout

- ~~§1 → §2: same seam, land together. §1 goes first.~~ §1 done; **§2 is the open half of that
  seam and the natural next item.**
- ~~§3 → §4, §3 → §9: same seam / same file.~~ All three done, landed together as planned.
- §4 → §6/§7: §4 introduces the app's first periodic git read. **It shipped restricted to the
  active repo** for exactly this reason, so §6 inherits a second consumer to route through the
  shared read gate before the warm set can be added to a reconcile tick.
- §3 → §7: the adaptive debounce now has a re-arm loop to interact with. `Drain` re-arms at
  `DebounceMs` while the activity gate is closed, so lengthening a slow repo's debounce also
  lengthens its deferral poll interval — the desired direction, but note that the two are now the
  same knob.
- §2 unblocks the measurement half of §6 and §7 (once there is one read per tick, per-repo read
  timing is meaningful).
- §5, §10 and §11 are independent of everything else and of each other, and can go in any order.
  §5 and §11 sit on the same seam (the mutation path) but touch disjoint files — §5 is view models,
  §11 is `GitService.cs` alone.
- §4 partially nets §5: a focused window's 30s reconcile tick broadcasts `WorkingTreeChangedMessage`
  for the active repo, which clears the hold C2 wedges. That bounds the frozen-panel window rather
  than closing it — the silent mutation errors are untouched, and an unfocused window still waits
  for a real filesystem event.
- §8 is independent and lowest priority.

**Still open from §3:** the gate-removal question. §3 established that the read-side gate is
redundant for every branch of `ClassifyGitChange` *except* a possible `git gc --auto` rewriting
`packed-refs`, and that was never verified empirically. Deferring rather than dropping is safe
either way, so this stays an optional follow-up — but §9 strengthened the case: the classifier, not
the gate, is now what suppresses read echoes on the `worktrees/` branch too.

**Symptom → item mapping**, for prioritising:

| Reported symptom | Closed by | State |
| --- | --- | --- |
| Branches shows a pull, pull button greyed out | §1 + §2 | §1 done, §2 open |
| Files in Unstaged that are not there | §3 (+ §5 for the mutation-failure variant) | §3 done, §5 open |
| File panel frozen after a stage + another op, until something else touches the tree | §5 (§4 bounds it to 30s while focused) | §4 done, §5 open |
| A stage/unstage that silently did nothing | §5 | open |
| Whole UI freezes opening Discard / Stash, or ticking Amend | §10 | open |
| A click during a fetch does nothing until the fetch ends | §11 | open |
| Unremembered / intermittent de-sync | §4 | done |
| General slowness on HDD | §1, §6, §7, §10, §11 (§9 if worktrees are in use; §8 optional) | §1, §9 done |
| A worktree's branch list / graph stale after an external checkout | §9 | done |

**For whoever picks up §2.** §3/§4/§9 touched none of §2's files — `GitService.cs`,
`LocalChanges.cs`, `RepoSnapshotStore.cs`, `RepoStatusStore.cs` are all as §1 left them, and §1's
*What §2 inherits* note still stands unchanged. One new interaction to be aware of: §4's reconcile
tick broadcasts `WorkingTreeChangedMessage` + `RefsChangedMessage` for the active repo every 30s,
so §2's "skip the now-redundant probe for the active repo on `WorkingTreeChangedMessage`" applies to
reconcile ticks as well — which is correct, since the tick's working-tree reload will carry the
summary once §2 lands. §4 already skips its own tick while a git read is in flight on that repo.
