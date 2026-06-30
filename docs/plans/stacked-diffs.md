# Stacked Diffs — review a large change as a chain of small increments

> Plan for backlog item #5 in `docs/diff-improvements.md`: "review a large change as a
> chain of small, individually-reviewable increments." Grounded in the current code; meant
> to be sliced further before implementation.

## What "stacked diffs" means here (scope decision)

The phrase has two industry meanings. This plan commits to the **review** one, because that
is what the backlog line describes and what GitBench is (a review-focused client):

- **In scope — range review.** Pick a `base` ref and a `head` ref. GitBench decomposes the
  range into its commits — *each commit is one increment* — and lets the reviewer step
  through them oldest→newest, one at a time, each rendered with the existing per-commit file
  list + diff, while tracking which increments have been reviewed (progress + "next
  unreviewed" navigation). This is "the History pane, scoped to `base..head`, ordered for
  reading, with reviewed-state." It reuses almost all existing diff machinery.

- **Out of scope (note, don't build):**
  - **Authoring stacks** (Graphite / git-branchless / Phabricator): creating and managing a
    chain of *dependent branches*, restacking after edits, submitting each layer. That's an
    authoring workflow, a much larger feature, and not what "review … as a chain" asks for.
  - **Semantic auto-split** of a single squashed diff into logical chunks. That's backlog
    item #3 (semantic diff) territory and needs heuristics we don't have. Git already gives
    us natural increments for free: the commits.

The key insight that makes this cheap: **a commit's diff is `git show <sha>` =
commit-vs-first-parent**, which GitBench already renders today via `DiffSide.Commit`
(`GitService.GetDiff`, `GitBench/Git/GitService.cs:2514-2522`). So the per-increment diff
needs **no new git plumbing** — only a way to (a) list the commits in a range and (b) drive
the existing commit-details surface from that list with reviewed-state on top.

## Grounding — what exists today

- **Diff data model** (`GitBench/Git/DiffResult.cs`): `DiffResult` is *one file* on one
  `DiffSide { Unstaged, Staged, Commit }` (`DiffResult.cs:3`). Multi-file is modeled as a
  list of `FileChange` (`Features/Commits/CommitDetails.cs:20`), each diffed lazily.
- **Per-commit diff** is already commit-vs-first-parent: `GetDiff(repo, path,
  DiffSide.Commit, sha)` → `git show --format= -M <sha> -- <path>`
  (`GitService.cs:2503,2514`). The "what to diff" unit is `DiffTarget(Path, Side, CommitSha?)`
  (`Features/Diff/DiffViewModel.cs:12`).
- **Commit listing** uses LibGit2Sharp: `GitService.Load(repo, cap)` walks
  `lg.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = refTips, SortBy =
  Topological|Time })` (`GitService.cs:87,220-236`). Per-commit metadata + file list:
  `LoadDetails(repo, sha)` (`GitService.cs:326`).
- **Range support is absent.** There is no `commitA..commitB` diff, no two-endpoint compare.
  The only range-ish primitive is `IsAncestor` → `git merge-base --is-ancestor`
  (`GitService.cs:1681-1687`). A `merge-base` that *returns the SHA* does not exist yet.
- **The History surface we will mirror**: `CommitHistory : Widget` → `HistoryView`
  (`Features/Commits/CommitHistory.cs:18,23`) is a draggable split — commit list
  (`CommitsPanelWidget`) on the left, `CommitDetailsView` on the right (`CommitHistory.cs:48-49`).
  `CommitDetailsView` does `ctx.Require<CommitDetailsViewModel>()` (`CommitDetailsView.cs:61`)
  and hosts the embedded `DiffView` via `Provide<DiffViewModel>{ Value = vm.DiffVm }`
  (`CommitDetailsView.cs:112`).
- **Selection → details** is message-bus driven: `CommitsViewModel` broadcasts
  `CommitSelectedMessage(repoId, sha)` (`CommitsViewModel.cs:99`), and
  `CommitDetailsViewModel.OnCommitSelected` (`CommitDetailsViewModel.cs:99`) loads that
  commit's details + drives its `DiffVm`. `StartLoad` (`CommitDetailsViewModel.cs:116`) does
  the background `LoadDetails`.
- **Navigation = mode switching, no router**: `enum MainViewMode { History, LocalChanges }`
  (`App/MainViewMode.cs`); `MainContent` is a `Switch<MainViewMode>` (`App/MainContent.cs:18`);
  the active mode is `State<MainViewMode>` registered via `context.AddService(new
  State<MainViewMode>(...))` (`App/AppServices.cs:32`) and toggled by `ModeSwitcherView`
  (`App/ModeSwitcherView.cs:51`) / `ModeSwitcherViewModel`.
- **DI is auto-constructing** (`framework/ZGF.Gui/Context.cs:95-137`): `ctx.Require<T>()`
  builds any public-ctor class transiently, injecting ctor params from context. So a new
  `ReviewViewModel` needs no registration — **but** a *range* (base/head) is data, not a
  service, so it cannot be a ctor param; it must flow through shared observable state, exactly
  like `State<MainViewMode>` does.
- **Async/stale-safe loads**: `ViewModelBase.RunBackground` + generation lanes
  (`Infrastructure/ViewModelBase.cs:80,48`); active-repo heavy data is cached/refreshed by
  `RepoSnapshotStore` (`Features/Repos/RepoSnapshotStore.cs`).
- **Entry-point seams**: branch context menus are built in `BranchesViewModel`
  (`BuildLocalBranchMenuItems(fullPath, isHead)`, returns `RepoBarContextMenu.Item(label,
  action, icon)` lists; branch rows carry `Name`/`TipSha`/`IsHead`). Modal dialogs:
  `ShowDialogMessage(Func<Action, IWidget>)` (`Messages/ShowDialogMessage.cs`) +
  `IDialogViewModel` with `CloseRequested` (pattern: `MergeBranchDialogViewModel`).

## Locked decisions

1. **Increment = commit.** The chain is the commit list of `base..head`, ordered oldest→
   newest (the natural reading order for review). Default to **first-parent** so a stack with
   merge commits reads as a linear chain of the feature's own commits.
2. **Per-increment diff reuses `DiffSide.Commit`.** Selecting increment *N* loads commit *N*'s
   files + diffs through the **existing** `CommitDetailsViewModel` / `DiffViewModel`. No new
   git diff plumbing in the MVP.
3. **New git reads, additive only.** (a) `MergeBase(repo, a, b)` → `git merge-base a b`;
   (b) `LoadReviewStack(repo, baseSha, headSha, cap)` → the range's commit list. Both mirror
   existing `GitService` patterns; nothing existing changes behavior.
4. **Surface = a new `MainViewMode.Review`** rendered by a `ReviewView` that mirrors
   `HistoryView`'s split (increment list left, the **shared** `CommitDetailsView` right).
   Recommended over a pop-out window because it maximizes reuse of `MainContent`'s `Switch`
   and the existing details/diff embedding. (Alternative — a dedicated review window à la
   `DiffWindowsViewModel` — noted in *Open decisions*.)
5. **The range flows through shared state, not a ctor param.** A new
   `State<ReviewSession?>` registered with `context.AddService(...)` (mirroring
   `State<MainViewMode>`) holds the active review's `(RepoId, BaseSha, HeadSha, labels)`.
   Entry points set it and flip `MainViewMode` to `Review`. `ReviewViewModel` subscribes and
   loads the stack — same shape as `CommitDetailsViewModel` subscribing to
   `CommitSelectedMessage`.
6. **Reviewed-state is per-increment and ephemeral in the MVP** — a `HashSet<string>` of
   reviewed SHAs in `ReviewViewModel`, reset when the range changes or the app restarts.
   Optional persistence is a later phase (a JSON store keyed by repo+base+head, modeled on
   `PreferencesStore`).
7. **Driving the right pane: a dedicated `CommitDetailsViewModel` instance, no bus
   cross-talk.** `ReviewView` constructs its own `CommitDetailsView` (which `Require`s its own
   transient `CommitDetailsViewModel`). Rather than broadcasting `CommitSelectedMessage`
   (which would also move the History pane's selection), expose a `public void Show(Guid
   repoId, string sha)` on `CommitDetailsViewModel` by extracting the body of the private
   `OnCommitSelected` (`CommitDetailsViewModel.cs:99-114`) and calling it directly. (The
   message subscription stays for the History pane.)
8. **Cumulative "net diff" of the whole range is a separate, optional mode** (Phase 6) — it is
   the *only* part that needs new diff plumbing (`git diff base...head -- path`). Kept out of
   the MVP so the core ships without touching `GetDiff`/`DiffSide`.

## Architecture

```
ReviewSession (shared State<ReviewSession?>, AppServices)          ← entry points set this
        │  (RepoId, BaseSha, HeadSha, BaseLabel, HeadLabel)
        ▼
ReviewViewModel : ViewModelBase<ReviewState>                       ← new
   • subscribes to the ReviewSession state + RefsChanged/WorkingTreeChanged
   • RunBackground → IGitService.LoadReviewStack(repo, base, head, cap)
   • holds: IReadOnlyList<ReviewIncrement>, SelectedSha, ReviewedShas (HashSet)
   • commands: SelectIncrement, ToggleReviewed, NextUnreviewed, Prev/Next
   • drives the right pane via _details.Show(repoId, sha)
        │
        ├── ReviewView : ContainerView (mirrors HistoryView split)  ← new
        │      • left:  ReviewListView (increments + reviewed checks + progress header + range bar)
        │      • right: CommitDetailsView (REUSED as-is → its own DiffView)
        │
        └── CommitDetailsViewModel.Show(repoId, sha)  ← new public method (extracted)
               → existing StartLoad → LoadDetails + DiffVm (DiffSide.Commit)   ← REUSED
```

New model types (alongside `CommitGraph.cs`'s records):

```csharp
public sealed record ReviewIncrement(
    string Sha, string ShortSha, string Summary, string Author,
    DateTimeOffset When, int FilesChanged);   // FilesChanged optional/lazy in MVP

public sealed record ReviewStack(
    Guid RepoId, string BaseSha, string HeadSha,
    string BaseLabel, string HeadLabel,
    IReadOnlyList<ReviewIncrement> Increments, bool Truncated);
```

## Implementation plan

### Phase 1 — Git layer (`IGitService` / `GitService`)

**1.1** `string? MergeBase(Repo repo, string a, string b)` → `git merge-base a b` via the
existing `RunGit` helper; trim, return null on failure. Mirrors `IsAncestor`
(`GitService.cs:1681`). Used to compute the default base for a branch review.

**1.2** `Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseSha, string headSha, int
cap)`. Mirror `Load` (`GitService.cs:87,220-236`) with a range filter:

```csharp
using var lg = new Repository(repo.Path);
var filter = new CommitFilter
{
    IncludeReachableFrom = headSha,
    ExcludeReachableFrom = baseSha,                 // ← this is "base..head"
    FirstParentOnly = true,                          // linear stack (decision #1)
    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
};
```

Collect up to `cap`, map to `ReviewIncrement`, then **reverse** so the list is oldest→newest.
`FilesChanged` can be left 0 in the MVP and filled lazily (or via a per-commit
`diff-tree --name-status` count) in a later polish pass. Resolve `BaseLabel`/`HeadLabel` from
the caller (branch names / short SHAs).

Both methods are additive; no existing call site changes.

### Phase 2 — Shared session state + mode (`App`)

**2.1** Add `Review` to `enum MainViewMode` (`App/MainViewMode.cs`).

**2.2** `record ReviewSession(Guid RepoId, string BaseSha, string HeadSha, string BaseLabel,
string HeadLabel)` and register `context.AddService(new State<ReviewSession?>(null))` in
`AppServices.cs` right beside the `State<MainViewMode>` line (`AppServices.cs:32`).

**2.3** `MainContent` (`App/MainContent.cs:18`): add the case
`MainViewMode.Review => new ReviewView()` (wrap in a `Widget` shell like `CommitHistory`).

**2.4** `ModeSwitcherView` (`App/ModeSwitcherView.cs`) + `ModeSwitcherViewModel`: add a third
`Review` segment. Keep it always present showing an empty-state when no session is set, *or*
gate its visibility on `State<ReviewSession?> != null` (simpler UX — see *Open decisions*).

### Phase 3 — Review view-model (`Features/Review/ReviewViewModel.cs`)

`internal sealed class ReviewViewModel : ViewModelBase<ReviewState>` where

```csharp
internal sealed record ReviewState(
    ReviewRenderState Render,        // Placeholder / Loading / Loaded(ReviewStack)
    string? SelectedSha,
    IReadOnlySet<string> ReviewedShas);
```

- Ctor deps (all context-resolvable): `IGitService, IRepoRegistry, IUiDispatcher,
  IMessageBus, ILocalizationService, State<ReviewSession?>`.
- Subscribe to the `State<ReviewSession?>` (load on change) and to `RefsChangedMessage` /
  `WorkingTreeChangedMessage` (reload the current range) — same triggers `RepoSnapshotStore`
  uses.
- `StartLoad`: `RunBackground(() => _git.LoadReviewStack(repo, session.BaseSha,
  session.HeadSha, cap))` on its own lane; on success store the stack and auto-select the
  **first unreviewed** increment (or the first).
- Owns the right-pane VM: construct a `CommitDetailsViewModel` (or have `ReviewView` own it)
  and call `Show(repoId, sha)` when `SelectedSha` changes.
- Commands: `SelectIncrement(sha)`, `ToggleReviewed(sha)`, `MarkReviewedAndAdvance()`,
  `NextUnreviewed()`, `Prev()`/`Next()`. Expose `IReadable<(int reviewed, int total)>` for the
  progress header.

### Phase 4 — `CommitDetailsViewModel.Show` (small refactor)

Extract the body of the private `OnCommitSelected` (`CommitDetailsViewModel.cs:99-114`) into
`public void Show(Guid repoId, string sha)` (and the clear path into `public void Clear()`).
`OnCommitSelected` then just forwards to them. This lets `ReviewViewModel` drive a dedicated
details instance directly without bus cross-talk (decision #7). Zero behavior change for the
History pane.

### Phase 5 — Review view (`Features/Review/`)

**5.1 `ReviewView : ContainerView`** — copy `HistoryView`'s split skeleton
(`CommitHistory.cs:23-141`, including the draggable divider + `CommitDetailsView` on the
right). The right pane is the **reused** `CommitDetailsView(ctx)`; provide the dedicated
`CommitDetailsViewModel` into its sub-context (a `Provide<CommitDetailsViewModel>` wrapper, or
construct `CommitDetailsView` with a child context that registers the instance — matching how
`HistoryView` lets `CommitDetailsView` `Require` it).

**5.2 Left pane `ReviewListView`** — a vertical list of increments. MVP can be a simple
list (one row per `ReviewIncrement`: reviewed check + short SHA + summary + author/date),
reusing row/list widgets rather than the heavy `CommitsView.Core` graph renderer (no lanes
needed for a linear stack). Above it: a **range bar** (`base ⟶ head`, with a "Change range…"
button) and a **progress header** ("3 / 8 reviewed"). Clicking a row → `SelectIncrement`;
the row shows a checkmark when in `ReviewedShas`.

**5.3 Empty state** — when `State<ReviewSession?>` is null: "Start a review from a branch's
context menu, or pick a range." with a button that opens the range dialog (Phase 7).

### Phase 6 — (Optional) cumulative "net diff" mode

A toggle in the range bar between **By increment** (the chain) and **Combined** (the whole
range as one diff, ignoring intermediate churn). This is the only piece needing new diff
plumbing:

- Extend the diff target to express two endpoints — either a new `DiffSide.Range` plus
  base/head SHAs on `DiffTarget`, or a parallel `RangeDiffTarget`. Add a `git diff
  base...head -- <path>` branch to `GetDiff` (`GitService.cs:2514`), and a range file-list via
  `git diff --name-status base...head`. `DiffContentView` renders the resulting `DiffResult`
  unchanged. Defer unless wanted — the chain view is the headline feature.

### Phase 7 — Entry points

**7.1 Branch context menu** — in `BranchesViewModel.BuildLocalBranchMenuItems` (and the
remote-branch builder) add a `RepoBarContextMenu.Item("Review changes…", () =>
StartReview(name, tipSha), LucideIcons.<icon>)`. `StartReview` computes the default base via
`MergeBase(repo, tipSha, GetHeadBranchName()-or-default)` (fall back to a dialog when no
sensible base), sets `State<ReviewSession?>`, and flips `MainViewMode` to `Review`.

**7.2 Range dialog** (general entry + "Change range…") — an `IDialogViewModel` with two ref
pickers (base, head) defaulting base = merge-base(head, upstream/main). On confirm, set the
session + switch mode. Follow `MergeBranchDialogViewModel` + `ShowDialogMessage(Func<Action,
IWidget>)`.

**7.3 (Later)** a history-list item ("Review from here to HEAD").

### Phase 8 — Keyboard + polish

- Increment nav keys on the list (reuse `ListArrowKbmController` like the file lists do):
  `]`/`[` or `j`/`k` next/prev increment; `r` toggle reviewed; `Enter`/`space` mark-reviewed-
  and-advance to next unreviewed.
- Progress persistence (optional): JSON store under `AppPaths.AppDataPath("review-state.json")`
  keyed by `repoId|baseSha|headSha`, modeled on `PreferencesStore` (atomic write,
  source-gen `JsonSerializerContext`). Note the caveat: SHA-keyed reviewed-state doesn't
  survive a rebase of the range (acceptable — a review session is tied to specific SHAs).
- Localization: add the new strings (range bar, progress, menu item, empty state) to the
  catalogs, per the existing `L.T(...)` / source-generated `Strings` flow.

### Phase 9 — Verification

- Build (`dotnet build`).
- Unit-test `LoadReviewStack` and `MergeBase` in `GitBench.Tests` (there is precedent —
  `HunkPatchBuilderTests`): empty range, single commit, linear stack, range with a merge
  commit (first-parent linearization), `base == head` (empty), bad refs (Failed).
- Manual via `/run`: review a multi-commit branch; step through increments; confirm each
  increment shows commit-vs-parent files + diff with syntax highlighting + hunk actions
  (inherited from `DiffView`); reviewed checkmarks + progress update; next-unreviewed lands
  correctly; range-change reloads; switching `MainViewMode` away and back preserves state
  (`MainContent` is `KeepAlive`).

## Touch list

| File | Change |
|------|--------|
| `Git/IGitService.cs`, `Git/GitService.cs` | `MergeBase`; `LoadReviewStack` (range RevWalk, first-parent, reversed) |
| `Features/Review/ReviewStack.cs` *(new)* | `ReviewIncrement`, `ReviewStack` records |
| `Features/Review/ReviewViewModel.cs` *(new)* | range load, selection, reviewed-state, nav, drives details |
| `Features/Review/ReviewView.cs` + `ReviewListView.cs` *(new)* | split mirror of `HistoryView`; increment list + range bar + progress; reuses `CommitDetailsView` |
| `Features/Commits/CommitDetailsViewModel.cs` | extract `Show(repoId, sha)` / `Clear()` from `OnCommitSelected` |
| `App/MainViewMode.cs` | add `Review` |
| `App/MainContent.cs` | `Switch` case → `ReviewView` |
| `App/AppServices.cs` | register `State<ReviewSession?>` (+ `ReviewSession` record) |
| `App/ModeSwitcherView.cs`, `App/ModeSwitcherViewModel.cs` | third segment |
| `Features/Branches/BranchesViewModel.cs` | "Review changes…" menu item + `StartReview` |
| `Features/Review/ReviewRangeDialog*.cs` *(new)* | base/head picker dialog |
| `Localization/*` | new strings |
| `GitBench.Tests/ReviewStackTests.cs` *(new)* | range-listing + merge-base tests |
| *(Phase 6, optional)* `Git/DiffResult.cs`, `Git/GitService.cs`, `Features/Diff/DiffViewModel.cs` | range diff (`git diff base...head`) |

## Commit slices

1. **Git layer**: `MergeBase` + `LoadReviewStack` + tests (no UI; verify via tests).
2. **Plumbing**: `MainViewMode.Review`, `State<ReviewSession?>`, `MainContent` case,
   `CommitDetailsViewModel.Show`. Temporarily set a hard-coded session to verify the right
   pane renders increments.
3. **Review UI**: `ReviewViewModel` + `ReviewView`/`ReviewListView` (list, range bar,
   progress, reviewed checks, selection driving details).
4. **Entry points**: branch menu item + range dialog + mode segment.
5. **Keyboard + (optional) persistence + localization polish.**
6. *(Optional, separable)* cumulative "net diff" mode (Phase 6).

## Open decisions (recommend, but worth confirming)

1. **Surface**: new `MainViewMode.Review` (recommended — max reuse of `MainContent`/details)
   vs a dedicated review **window** (mirrors `DiffWindowsViewModel`, lets a review sit beside
   the main window) vs an overlay panel inside History.
2. **Third mode-switcher segment**: always visible with empty-state, vs only shown while a
   review session is active (cleaner, since review is transient).
3. **Default base for "Review changes…" on a branch**: merge-base with the branch's *upstream*
   vs merge-base with the repo's *default/main* branch. (Upstream is more correct for a
   feature-vs-its-PR-target review; fall back to a dialog when neither resolves.)
4. **Reviewed-state persistence** in the MVP (ephemeral) vs persisted from day one.
5. **First-parent vs full walk** for the increment list when the range contains merges
   (recommended: first-parent for a linear, readable stack).
