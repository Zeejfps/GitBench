# UI coherence on slow disks

> On a 5400 RPM HDD the app visibly de-syncs: the Branches view shows a pending pull while the
> pull button is greyed out, files linger in Unstaged after they are gone, and everything is
> sluggish. None of these are HDD-specific bugs — they are mostly latent races whose windows are
> normally sub-100ms on an SSD and become seconds-wide when every `git status` costs seconds. One
> (A) is not a race at all: it is the same number stored in four places on mismatched refresh
> triggers, and it does not self-correct at any disk speed. This document records the root causes
> found by reading the sync path end to end, and the work items that close them.
>
> **Status: analysis only. Nothing here is implemented.** Items are ordered by what fixes the
> reported symptoms first; §1+§2 are one seam and should land together, as should §3+§4.

## Root causes

### A. Ahead/behind has four sources of truth

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

### B. The watcher silently drops real filesystem events, permanently

`RepoWatcher.ScheduleWorkingTree` (`RepoWatcher.cs:266`) returns early when
`IRepoActivityTracker.IsActive` — i.e. whenever any git process is running on that repo, plus a
500ms tail. `RepoActivityTracker`'s own comment (`RepoActivityTracker.cs:22-26`) argues this is
safe because "the in-flight reload's `git status` will see the user's change."

That holds only for edits landing *before* status stats that path. An edit landing after is
dropped at the FSW level and **never recovered — there is no reconcile poll anywhere in the app.**
The file stays in the list indefinitely.

The gate is opened by `GitProcessRunner.Run` for *every* invocation, reads included
(`GitProcessRunner.cs:74`). On an SSD a status read is ~50ms, so the gate is closed rarely; on a
slow HDD it is seconds, and reads fire on every tick, so the gate is closed a large fraction of
the time. **The drop rate scales directly with disk latency. This is the phantom-unstaged-files
report.**

### C. Index mutations are not serialized per repo

`RunIndexMutation` (`LocalChangesViewModel.cs:1067`) calls `RunOutcome` → `RunBackground`, which
fires `Task.Run` unconditionally with no exclusivity (`ViewModelBase.cs:80`). Two fast stage
clicks — or a stage overlapping a discard, commit, or pull — run concurrent `git add` processes
on one repo, producing `Unable to create '.git/index.lock'`. `ApplyOptimisticMove` has already
moved the row by then, so a failure leaves the list wrong. The lock window is invisible on an SSD
and routinely hit on a slow HDD.

Related: `RunBackground` drops `onResult` when the lane is stale, so a superseded mutation never
broadcasts its `WorkingTreeChangedMessage` — and that broadcast is the only thing that clears
`_deferStoreReloadUntilWorkingTreeChange` (`LocalChangesViewModel.cs:88`), which gates whether
store snapshots are allowed to land at all.

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

---

## Work items

### 1. HEAD's sync counts get exactly one owner

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
- **Also fixes:** the missing trigger from root cause A — probe on active-repo change, so a switch
  never leaves the toolbar showing a probe from before the switch.
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

### 3. Never drop a watcher signal; defer it

Change `ScheduleWorkingTree` (and the sibling `ScheduleRefs` / `ScheduleWorktrees` /
`ScheduleSubmodules`) from `if (IsOurOwnWrite()) return;` to setting a per-channel "pending" flag
and re-arming the debounce for after the activity window closes. Costs one bool per channel; no
external edit is lost regardless of disk latency.

- **Files:** `RepoWatcher.cs`, possibly `RepoActivityTracker.cs` (needs a "notify when quiet" hook
  or the debounce re-arms on a timer).
- **Acceptance:** an edit made while a git read is in flight still produces exactly one debounced
  reload after the read completes. Closes root cause B.
- **Open question:** it may be possible to narrow or remove the read-side gate entirely rather
  than defer. The `.git` classifier already ignores `index`, `modules/*/index`, `objects`, and
  `logs` by path (`RepoWatcher.cs:154-218`), and the tree watcher excludes any `.git` segment, so
  a read's write-echo appears to be filtered on path alone already. **This was not verified
  empirically** — verify before relying on it. Deferring rather than dropping is safe either way,
  since a deferred event still only fires one debounced reload.

### 4. Reconcile safety net

Add a low-frequency revalidate: on window focus-gain, and every ~30s while the window is focused.
Any missed signal self-heals within one interval instead of persisting until the user switches
repos.

- **Files:** new hosted service alongside `RepoWatcherService`; needs a window-focus signal from
  the framework.
- **Acceptance:** no UI state can stay stale indefinitely. This is what structurally removes the
  "other weird de-sync issues I can't remember" category.
- **Note:** land with §3.

### 5. Serialize index mutations per repo

Put a per-repo queue in front of stage / unstage / discard / commit so only one index-mutating
git process runs per repo at a time. Eliminates `index.lock` collisions and makes optimistic
state reliably reconcilable.

- **Files:** `LocalChangesViewModel.cs` (`RunIndexMutation`, `RunIndexOp`, `RunUnstageWithReset`),
  possibly a new per-repo mutation queue service; interaction with `RepoOperationsStore` (push /
  pull also touch the index).
- **Acceptance:** rapid stage clicks on a slow disk never produce `index.lock` errors, and a
  failed mutation always reconciles the optimistic list. Closes root cause C.
- **Watch:** the `_deferStoreReloadUntilWorkingTreeChange` clearing path — a mutation whose
  `onResult` is dropped as stale never broadcasts, so the flag must not depend solely on that
  broadcast.

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

---

## Dependency notes for task breakout

- §1 → §2: same seam, land together. §1 goes first — it closes the reported symptom and is
  self-contained.
- §3 → §4: same seam, land together. §4 is the insurance policy that makes the whole class of
  symptom non-persistent, so do not defer it far behind §3.
- §2 unblocks the measurement half of §6 and §7 (once there is one read per tick, per-repo read
  timing is meaningful).
- §5 is independent of everything else and can go in any order.
- §8 is independent and lowest priority.

**Symptom → item mapping**, for prioritising:

| Reported symptom | Closed by |
| --- | --- |
| Branches shows a pull, pull button greyed out | §1 + §2 |
| Files in Unstaged that are not there | §3 (+ §5 for the mutation-failure variant) |
| Unremembered / intermittent de-sync | §4 |
| General slowness on HDD | §1, §6, §7 (§8 optional) |
