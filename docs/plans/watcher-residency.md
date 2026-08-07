# Watcher residency — bounding GitBench's share of a system-wide budget

> Prompted by a Linux bug report: with GitBench open, *other applications* could no longer start
> or watch files. GitBench had consumed the user's entire inotify budget. Two contributing
> defects are already fixed (below); this plan covers the structural one — that the number of
> watchers GitBench holds is a function of how many repos the user has open, and nothing else.

## The problem

`RepoWatcherService` creates a `RepoWatcher` per entry in `IRepoRegistry.Repos`
(`GitBench/Features/Repos/RepoWatcherService.cs:75`) with no cap, and worktrees and submodules
are entries in that same list (`RepoRegistry.cs:535`, `:610`). Each watcher is a recursive
`FileSystemWatcher` rooted at the working tree.

On Windows and macOS that is nearly free — `ReadDirectoryChangesW` and FSEvents each cover a
recursive tree with one handle, and the cost is per-process. On Linux it is not free and it is
not per-process:

| Resource | Cost | Default limit | Scope |
| --- | --- | --- | --- |
| `fs.inotify.max_user_instances` | 1 per `FileSystemWatcher` | 128 | **per user, all processes** |
| `fs.inotify.max_user_watches` | 1 per **directory** in the subtree | 8192–524288 by distro | **per user, all processes** |

Both pools are shared with the user's editor, file manager, IDE, shell tooling and systemd user
units. GitBench exhausting them is not a GitBench outage; it is a desktop-session outage. That is
what the reporter experienced.

### Already fixed (do not re-derive)

1. **The duplicate `.git` watcher is gone.** Each repo used to get a second recursive watcher
   rooted at `.git`, whose subtree the working-tree watcher already covered — `IsUnderGit`
   filtered those events at *delivery*, but the kernel watches were allocated regardless. `.git`
   paths now route off the tree watcher into the existing `ClassifyGitChange`
   (`RepoWatcher.cs`, `OnTreeEvent` / `OnTreeRenamed`). Halves instances per repo and removes a
   duplicate watch on every directory under `.git`, including the 256-way `objects/` fanout.
2. **Failures are reported, not swallowed.** `WatcherDiagnostics` names the failing repo, the
   exception (whose .NET message already identifies which inotify limit was hit), the values read
   from `/proc/sys/fs/inotify/max_user_*`, and how many instances GitBench itself holds. It also
   warns once when GitBench crosses half the instance limit.

Those two together take a 5-primary / 20-submodule session from 50 instances to 25, and cut the
watch count roughly in half. They do not bound anything. Enough repo entries still walks past 128
instances, and *any* session still takes an unbounded share of `max_user_watches` — which, at one
watch per directory, is the limit that runs out first.

## The proposal: a bounded resident set

Stop treating "known to the registry" as "watched". Maintain a **resident set** of at most *K*
watchers, filled by priority, recomputed when the active repo or the registry list changes.

### Priority order

One flat ordering over every `Repo` entry; take the top *K*:

1. The active repo itself.
2. The rest of the active repo's family — its primary (walk `ParentRepoId` up) and that primary's
   worktrees and submodule forest (`IRepoRegistry.GetWorktrees` / `GetSubmodules`).
3. Other primaries, most-recently-active first.
4. Other primaries' children, if slots remain.

A flat ordering rather than "resident families" deliberately: a primary with 40 submodules would
otherwise be an all-or-nothing 41-instance commitment. Here it simply fills the budget after the
thing the user is actually looking at, which is the correct outcome.

### Choosing *K*

`WatcherDiagnostics` already reads `max_user_instances`. Derive `K = clamp(4, 32, limit / 4)` on
Linux — never more than a quarter of the user's instance budget. Off Linux there is no shared
budget to protect, so default *K* high enough to be a no-op for today's users, while keeping the
same code path so the policy is testable on any platform.

**The instance budget is not the binding constraint, though.** 32 watchers on a 20k-directory
monorepo is 640k watches against a 524288 limit. Instances are easy to count; watches are the
ones that actually run out first, and we cannot know a repo's directory count without walking it.

Two options, in preference order:

- **Measure, don't estimate.** On Linux each inotify fd's `/proc/self/fdinfo/<fd>` lists one
  `inotify wd:` line per watch held. Summing those across our own fds gives GitBench's *exact*
  current watch consumption, at the cost of reading a few small files. Poll it when the resident
  set changes and evict down when we exceed a fraction of `max_user_watches`. **Verify this file
  format before building on it** — it is stable in practice but is not a documented ABI.
- **Fall back to instance-count alone** if the above doesn't hold up, and accept that a single
  giant repo can still exhaust watches. That case is what the separate "targeted `.git` watches /
  exclude `objects/`" item on the backlog addresses; residency and subtree-narrowing are
  complementary, not alternatives.

### Eviction and thrash

Evict least-recently-active first, never the active repo or its family. Alt-tabbing between two
repos must not churn watchers: on Linux, creating a recursive watcher walks the whole tree adding
watches, so a create is expensive and a create/dispose cycle per switch would be worse than the
problem. Apply hysteresis — a grace period (~30s) before a fallen-out-of-set repo is actually
torn down, and evict only when genuinely over budget.

## Re-attach: reconcile is already built

The obvious objection is that an unwatched repo goes stale, and a re-watched one has a hole in its
history. Both are already handled by machinery in the tree:

- **On re-attach, call `RepoWatcher.ScheduleAllChannels()`.** That is the existing
  FSW-buffer-overflow recovery path — "nothing is known about any channel, arm all four and let
  the UI reconcile." A watcher gap is the same epistemic state as a dropped-events overflow, so it
  wants the same recovery, and it flows through the same debounce and activity gate.
- **While unwatched, `RepoReconcileService` covers staleness.** It already re-broadcasts the
  ordinary change channels for the active repo every 30s and on app-foreground gain, and its
  header comment names this exact case: "an FSW that failed to attach"
  (`RepoReconcileService.cs:8-10`, `:20`). Non-resident repos are not covered by it today — it
  reconciles the active repo only. Widening it to cover evicted repos on a slower cadence is the
  natural companion change, and is where the real staleness cost of this plan lands.

**What genuinely degrades:** a background repo's RepoBar row (dirty marker, ahead/behind) can lag
by up to the reconcile interval instead of updating within the debounce window. That is the price,
it is bounded, and it is worth it.

## Phases

1. **Extract the seam.** Give `RepoWatcherService` a watcher factory it can be tested against, and
   an injected clock for recency and hysteresis. No behavior change; this is the phase that makes
   the rest testable, since the service has no tests today.
2. **Residency policy as a pure function.** `(repos, activeId, lastActiveTimes, K) → ordered
   resident set`. Fully unit-testable with no filesystem. Pin the family-priority rule, the
   40-submodule case, and that the active repo is never evictable.
3. **Wire it in.** Recompute on `Active` change and on registry list change; diff against the
   current set; dispose evictions (after hysteresis) and `ScheduleAllChannels()` on additions.
4. **Budget derivation.** *K* from `max_user_instances`; validate and then add the
   `/proc/self/fdinfo` watch measurement and watch-driven eviction.
5. **Widen `RepoReconcileService`** to sweep non-resident repos on a slower cadence, so an evicted
   repo's row still converges.

## Open questions

- **Does re-attach double-load?** Activating a repo already triggers loads through
  `RepoSnapshotStore`; `ScheduleAllChannels()` on the newly resident watcher may duplicate that
  work. Check whether the stores dedupe, and if not, suppress the reconcile when the repo is
  becoming resident *because* it just became active.
- **Should *K* be user-visible?** A setting is easy to add and hard to remove. Prefer deriving it
  and reporting via `WatcherDiagnostics`; revisit only if real sessions hit the cap.
- **Child watchers may be partly redundant.** A submodule's working tree sits inside its parent's
  recursive watch, so the parent already sees those file events — it just attributes them to the
  parent. Worth measuring whether per-child watchers earn their cost, or whether the parent's
  events could be re-attributed by path prefix. If they can, the whole child tier disappears from
  the budget, which would be a larger win than residency itself.

## Rejected

- **A global watcher count cap with no policy.** Bounds the damage but makes which repos work a
  function of registry order. Residency at least makes it a function of what the user is doing.
- **Watching nothing and polling everything.** Correct on every platform and unacceptable on
  none-Linux ones, where watching is cheap and instant refresh is the product.
- **Shipping a Linux-only code path.** The policy runs everywhere with a platform-derived *K*, so
  it is exercised on the maintainer's own machine rather than only in the environment where it
  matters and is hardest to reproduce.
