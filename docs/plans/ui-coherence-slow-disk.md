# UI coherence on slow disks

> On a 5400 RPM HDD the app visibly de-syncs: the Branches view shows a pending pull while the
> pull button is greyed out, files linger in Unstaged after they are gone, and everything is
> sluggish. None of these are HDD-specific bugs — they are latent races whose windows are
> normally sub-100ms on an SSD and become seconds-wide when every `git status` costs seconds.
> This document records the root causes found by reading the sync path end to end, and the work
> items that close them.
>
> **Status: analysis only. Nothing here is implemented.** Items are ordered by what fixes the
> reported symptoms first; §1+§2 are one seam and should land together, as should §3+§4.

## Root causes

### A. Ahead/behind has two independent sources of truth

The same fact is computed by two different git processes, launched at different times, refreshed
on different triggers, and stored in different stores:

| Consumer | Source | Path |
| --- | --- | --- |
| Branches view badges | `git for-each-ref --format=%(upstream:track)` | `GitService.cs:789` → `BranchListing` → `IRepoSnapshotStore` → `BranchListRow.cs:150` |
| Push/pull button enablement | `git status --porcelain=v2 --branch` | `GitService.cs:1127` → `GitStatusSummary` → `IRepoStatusStore` → `ActionsToolbarViewModel.cs:150` |

They are never guaranteed to agree. The disagreement window is however long it takes the slower
of the two reloads to land — imperceptible on an SSD, seconds on a slow HDD. **This is the
"branches says I have a pull, the pull button is grey" report.**

`RepoSnapshotStore.PatchHeadSync` (`RepoSnapshotStore.cs:240`) already exists specifically to
paper over this, but only fires on the optimistic post-push/pull message, not on the general case.

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

### 1. One observation, one timestamp

Add `--branch` to the file-list status call and return branch / ahead / behind / dirty alongside
the file lists from that single read. The active repo's `RepoStatus` becomes a projection of the
same snapshot the file lists came from, rather than an independent probe. The cheap all-repos
probe stays for *non-active* repos, where file lists are not loaded.

- **Files:** `GitService.cs` (`GetLocalChanges`, `RunGitStatusPorcelain`, `ParseStatusSummary`),
  `IGitService.cs`, `RepoSnapshotStore.cs` (`LocalChangesData`), `RepoStatusStore.cs`.
- **Also fixes:** halves per-tick disk work for the active repo (root cause E, first half).
- **Acceptance:** the active repo's file lists and its ahead/behind/dirty signals can never
  originate from two different git invocations.

### 2. Make `for-each-ref` non-authoritative for HEAD

Generalize `PatchHeadSync` so the branch listing's HEAD row always renders the status summary's
numbers rather than its own. Non-HEAD branches keep using `for-each-ref` — `git status` cannot
report them.

- **Files:** `RepoSnapshotStore.cs` (`PatchHeadSync` and its trigger set), `BranchesViewModel.cs`.
- **Acceptance:** the Branches view HEAD badge and the toolbar push/pull enablement cannot
  disagree by more than a frame. Closes root cause A.
- **Note:** land with §1 — same seam.

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

- §1 → §2: same seam, land together.
- §3 → §4: same seam, land together. §4 is the insurance policy that makes the whole class of
  symptom non-persistent, so do not defer it far behind §3.
- §1 unblocks the measurement half of §6 and §7 (once there is one read per tick, per-repo read
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
