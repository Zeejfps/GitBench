# Filesystem watching — what it costs, and what we actually need

> Prompted by a Linux bug report: with GitBench open, *other applications* could no longer watch
> files or start. GitBench had consumed the user's entire inotify budget.
>
> This replaces an earlier draft that proposed rationing watchers — an LRU "resident set" with
> eviction and hysteresis. Checking it against prior art killed it: nothing comparable does
> anything like that, because a git client does not need deep filesystem watching at all.

## What watching costs

`RepoWatcher` creates one recursive `FileSystemWatcher` per entry in `IRepoRegistry.Repos`, and
worktrees and submodules are entries in that same list (`RepoRegistry.cs:535`, `:610`).

On Windows and macOS that is nearly free — `ReadDirectoryChangesW` and FSEvents each cover a
recursive tree with one handle, and the cost is per-process. On Linux it is neither.

| Resource | Cost | Default limit | Scope |
| --- | --- | --- | --- |
| `fs.inotify.max_user_instances` | 1 per `FileSystemWatcher` | 128 | **per user, all processes** |
| `fs.inotify.max_user_watches` | 1 per **directory** in the subtree | 8192–524288 by distro | **per user, all processes** |

Both pools are shared with the user's editor, file manager, IDE, shell tooling and systemd user
units. GitBench exhausting them is not a GitBench outage; it is a desktop-session outage.

### Already landed (`85b4f31`)

- **The duplicate `.git` watcher is gone.** Each repo used to get a second recursive watcher rooted
  at `.git`, whose subtree the working-tree watcher already covered. `.git` paths now route off the
  tree watcher into the existing `ClassifyGitChange`. (Phase 2 partly re-splits this *on Linux* — a
  narrow, non-recursive gitdir root comes back there, because the tree watch that made it a
  duplicate is exactly what Linux no longer does. Windows and macOS stay consolidated.)
- **Failures are reported, not swallowed.** `WatcherDiagnostics` names the failing repo, the
  exception, the values read from `/proc/sys/fs/inotify/max_user_*`, and how many instances
  GitBench holds — and warns once when it crosses half the instance limit.

Both are worth keeping. Neither bounds anything.

### Also landed

- **Focus gain now reconciles every channel and defers rather than drops** — see *Focus and poll*
  below. Platform-independent, and a prerequisite for the Linux change being a fix rather than a
  regression.

## What comparable software does

- **Git itself** ships a builtin filesystem monitor that supports Linux via inotify. Its
  documentation has a `LINUX CAVEATS` section, and that section's entire content is: the default is
  ~8192 watches, large repos exceed it, here is the `sysctl` to raise it. The git project's answer
  to this exact problem is *document the tunable*.
- **VS Code** is the maximal-effort version: a dedicated native recursive watcher (parcel), a
  `files.watcherExclude` setting whose defaults already exclude `node_modules` and `.git/objects`,
  a wiki page titled "File Watcher Issues," and a user-facing "unable to watch for file changes"
  error. It *still* has long-running open bugs that the excludes don't reduce the inotify count
  (microsoft/vscode#50408, #50417, #196566).
- **GitHub Desktop** — our closest peer — doesn't deep-watch at all. Status refreshes on window
  focus regain (desktop/desktop#2790), plus an opt-in "periodically fetch and refresh status for
  all repositories" setting.
- **SourceTree** has no Linux build; not a datapoint.

The field splits by what the app needs, not by how much it cares:

- **Editors and build tools** need per-keystroke fidelity, so they own the native watcher layer,
  exclude at registration, *and still* tell users to raise the sysctl. Expensive, still imperfect.
- **Git clients** need "did anything change" at human latency, so they use focus and poll.

GitBench is the second and built the first — on .NET's `FileSystemWatcher`, which has none of the
exclusion machinery the first camp depends on. `Filter`/`Filters` match filenames at event
*dispatch*; `IncludeSubdirectories` is all-or-nothing. There is no "recursive, except under here."

## The approach

Two changes, neither needing a custom inotify layer:

1. **On Linux, narrow the watch roots** so cost is proportional to refs, not to the working tree.
2. **Everywhere, make focus and poll the primary refresh path** rather than a backstop.

### Linux watch roots

Per repo, replacing the one recursive watch on the working tree — `RepoWatchRoots.For` with
`WatchCost.RecursiveIsOneWatchPerDirectory`:

| root | recursive | ~watches | catches |
| --- | --- | --- | --- |
| the real gitdir | no | 1 | `HEAD`, `packed-refs`, `FETCH_HEAD`, `ORIG_HEAD`, `MERGE_HEAD`; creation/removal of `refs/`, `worktrees/`, `modules/` |
| `<gitdir>/refs` | yes | tens | loose ref moves — branches, remotes, tags |
| `<gitdir>/worktrees` | yes, when present | small | worktree add/remove, per-worktree `HEAD` |
| working-tree root | no | 1 | `.gitmodules` only — see below |

Roughly 5–50 watches per repo instead of 10k–50k. Deliberately **not** watched:

- **`objects/`** — the largest part of most gitdirs, and useless to us; `ClassifyGitChange` already
  ignores every path under it.
- **`modules/` recursively** — that is each submodule's entire gitdir, objects included. Submodules
  are their own registry entries and get their own roots. A non-recursive watch on the gitdir
  already reports `modules/` itself appearing or vanishing.
- **the working tree** — the whole point.

**The root watch acts only on `.gitmodules`.** A non-recursive root watch costs 1 watch and would
also report top-level file edits, but acting on those would make refresh latency depend on how deep
a file happens to be — worse than uniformly polled. It exists solely so an external `.gitmodules`
edit still triggers submodule rediscovery, the one working-tree signal with no polling equivalent.

**Blocker — `RepoWatcher` assumed `<repo>/.git` is a directory.** `_gitDirPrefix` was built as
`repo.Path + "/.git/"`. For worktrees and submodules `.git` is a gitlink *file* pointing elsewhere,
so those entries had no gitdir classification at all — they relied on the primary's
`RefsChangedMessage` fan-out. **Landed** as `RepoGitDir.Resolve`: read and parse the gitlink rather
than shelling out to `git rev-parse --absolute-git-dir`, so there is no process spawn per repo while
the registry loads and it still works when git isn't on `PATH`. Falls back to `<repo>/.git` for
anything unreadable, which is exactly the old assumption, so it can't regress the primary case.

### Focus and poll — the mechanism already exists

`RepoReconcileService` already re-broadcasts the ordinary change channels on a 30s tick **and on
app-foreground gain, on every platform**. Its header comment already names the case this plan leans
on: "an FSW that failed to attach." What was short is the coverage.

**Landed:** a tick and a focus gain are now distinct events. A tick stays cheap liveness — two
channels, and droppable when git is busy, because another tick is one interval behind it carrying
the same information. A focus gain is one-shot, so it covers **all four channels** (a worktree added
or submodule updated from a terminal has no other route in, least of all on Linux) and is
**postponed rather than dropped** when a git op is in flight — the same defect `RepoWatcher`'s drain
was fixed for. One retry outstanding at a time, so a long op can't build a queue.

**Decided: the reconcile covers the active repo only.** Extending it to every repo would turn one
focus gain into N git reads, and focus gains are frequent. The exposure is narrower than it looks —
narrow roots are per repo *entry*, not per active repo, so every open repo still watches its own
refs for a handful of watches and ahead/behind keeps updating. What goes stale on a background row
is working-tree dirtiness, until that repo is activated and loads for real.

## Phases

1. **Resolve the real gitdir** for every repo kind. ✅ **Landed** — `RepoGitDir.Resolve`, pinned
   against real git for a primary, a linked worktree and a submodule, plus the gitlink's shapes
   (absolute target, relative target, forward slashes, loose spacing) and its fallbacks.
2. **Narrow roots on Linux.** ✅ **Landed** — `RepoWatchRoots.For(workingTree, gitDir, cost)`, a pure
   function taking the cost as an argument rather than reading the OS, so both layouts are reachable
   from a unit test on any platform: the Linux branch is the one nobody developing this can run.
   Windows and macOS keep the single recursive tree watcher. The classifier and the debounce channels
   are untouched, since `ClassifyGitChange` already takes gitdir-relative paths and already ignores
   everything the narrow roots exclude.
3. **Measure.** Not started; needs a Linux machine. Count watches actually held, report through
   `WatcherDiagnostics`, and confirm both the drop and that no channel went silent.

### What phases 1 and 2 actually built

- `RepoGitDir` — the gitlink parser, and the only thing that knows `<repo>/.git` might be a file.
- `RepoWatchRoots` — `WatchCost` (one handle vs one watch per directory), `WatchRootKind`
  (`WorkingTree` / `GitDir` / `Gitmodules`), `WatchRoot`, and the pure layout function.
- `RepoWatcher` — one `FileSystemWatcher` per root instead of one per repo, with a handler pair per
  kind: the recursive tree root splits by path as before, a gitdir root goes straight to the
  classifier, and the non-recursive working-tree root acts on `.gitmodules` alone.

### Known gaps in what landed

- **Narrow roots never re-attach.** `refs/` and `worktrees/` are attached only if they exist at
  construction. A `git worktree add` into a repo that had no `worktrees/` yet leaves that root
  unwatched until the app restarts — mitigated because the new worktree becomes its own registry
  entry with its own roots, and because the non-recursive gitdir root still reports the directory
  appearing. Same story if `refs/` is deleted and recreated wholesale.
- **`ClassifyGitChange` ignores a bare `refs` path.** The roots table above claims the non-recursive
  gitdir root catches `refs/` being created or removed; it sees the event, but the classifier maps
  only `refs/…`, not `refs`. `worktrees` and `modules` bare *are* mapped. Left alone deliberately —
  changing the classifier was out of scope for these phases.
- **Worktrees and submodules gain their own ref detection on Linux only.** Their gitdir sits under
  the *primary's* `.git`, so a single recursive watch on their own working tree never covered it, and
  on Windows/macOS that is still the whole layout. Those platforms keep relying on the primary's
  `RefsChangedMessage` fan-out, which is what they did before. Resolving the gitdir is necessary
  either way — without it the narrow roots would point at a directory holding none of their refs.
- **On Linux a submodule's HEAD moving no longer reaches the primary.** `modules/` is deliberately
  unwatched, so where the recursive tree watcher used to see `<primary>/.git/modules/<name>/refs/…`
  and raise `SubmodulesChangedMessage(primary)`, the submodule's own entry now catches it and raises
  `RefsChangedMessage(submodule)` — different message, different repo id, so the primary's submodule
  view doesn't refresh. Submodules being *added or removed* are still caught, via the non-recursive
  gitdir root seeing `modules` appear or vanish. The primary self-heals on focus gain, which
  broadcasts the submodules channel; the 30s tick does not.
- **The layout trades the watch budget for the instance budget.** Each repo is now 2–4
  `FileSystemWatcher`s where it was 1 — every one an inotify instance out of a 128-per-user pool —
  against ~5–50 watches where it was 10k–50k. Overwhelmingly the right trade, since watches was the
  binding constraint by three orders of magnitude, but instances are the binding one now: roughly 40
  repo entries rather than 128. The `.gitmodules` root is skipped when the file is absent to claw
  some of that back. If the instance ceiling ever bites, the next cut is folding `refs/` into the
  gitdir root and leaning on `packed-refs`.
- **`WatcherDiagnostics.Created` now counts roots, not repos.** That is the right number for the
  instance budget, but its warning text still says "watching N repositories". Phase 3's problem.

## What degrades

On Linux a local edit no longer appears instantly — it appears on focus gain, on the next reconcile
tick, or after any in-app git op. That is GitHub Desktop's behavior and the norm for the category.
Windows and macOS are unchanged.

If the interval feels bad in practice, the answer is a shorter tick for the active repo, not a
return to watching the tree.

## Open questions

- **Is 30s right once it is load-bearing rather than a backstop?** Probably shorter for the active
  repo, unchanged for the rest. Measure first — `git status` on a large repo on a slow disk is
  exactly what the read gate's EWMA exists for.
- **Does `refs/` stay small?** A repo with thousands of loose refs creates a directory per namespace
  segment. `packed-refs` collapses this and `git gc` packs aggressively, but a freshly-fetched repo
  may briefly be large. If it bites, drop to non-recursive watches on `refs/heads` and
  `refs/remotes` and lean on `packed-refs`.
- **Should Linux keep a broader working-tree watch?** A non-recursive root watch is 1 watch and
  covers "small repo, edits at top level" nicely. Rejected above for latency-consistency, but it is
  cheap to reverse if users ask.

## Rejected

- **Watcher residency (LRU resident set, eviction, hysteresis).** The previous draft. It rations a
  resource instead of not consuming it: needs a clock, a policy, an eviction path and a re-attach
  reconcile — and after all that, one monorepo can still exhaust `max_user_watches` alone. Narrow
  roots make the budget a non-issue instead of a thing to manage.
- **Reconciling every repo on focus gain.** Focus gains are frequent and this makes each one cost a
  git read per open repo. The active repo is the one the user is looking at; the rest can wait for
  activation.
- **Gitignore-driven subtree exclusion.** The biggest theoretical lever, unreachable through
  `FileSystemWatcher`, and the open VS Code bugs above show it is hard to get right even with a
  purpose-built native watcher. Note also that matching a gitignore rule does not mean git ignores
  the file — ignore rules apply only to *untracked* files, so the naive filter silently hides edits
  to tracked files that match a pattern.
- **Driving inotify directly.** What VS Code and JetBrains do. Justified for an editor; not for a
  client that needs only human-latency change detection.
- **Telling users to raise the sysctl and calling it done.** Legitimate — it is literally git's
  documented answer, and `WatcherDiagnostics` now says so. But git's daemon is opt-in and per-repo,
  and exists to speed up `git status` on monorepos. We watch every open repo by default, so the
  default has to be cheap.
