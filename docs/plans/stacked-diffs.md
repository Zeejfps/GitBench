# Stacked Diffs — a dedicated Review window

> Plan for backlog item #5 in `docs/diff-improvements.md`: "review a large change as a chain of
> small, individually-reviewable increments." **Re-framed:** instead of a new in-place view mode,
> we build GitBench's own **Review window** — a real second OS window, launched from a branch
> context menu the way "Merge X into Y…" is — that walks a branch's commits as a reviewable
> *stack*. Phases are ordered **outside-in** so every phase produces something runnable and
> locally testable before the next one starts.

## What "stacked diffs" means here (scope)

The phrase has two industry meanings. This plan commits to the **review** one, because that is
what the backlog line describes and what GitBench is (a review-focused client):

- **In scope — range review in a dedicated window.** Pick a `base` ref and a `head` ref. GitBench
  decomposes the range into its commits — *each commit is one increment* — and lets the reviewer
  step through them oldest→newest in a purpose-built window, each rendered with a per-commit file
  list + diff, while tracking which files/increments have been reviewed (progress + "next
  unreviewed" navigation). It reuses almost all existing diff machinery and the existing
  multi-window plumbing.

- **Out of scope (note, don't build):**
  - **Authoring stacks** (Graphite / git-branchless / Phabricator): creating and managing a chain
    of *dependent branches*, restacking after edits, submitting each layer. That's an authoring
    workflow and not what "review … as a chain" asks for.
  - **Semantic auto-split** of a single squashed diff into logical chunks. That's backlog item #3
    territory. Git already gives us natural increments for free: the commits.

Two insights make this cheap:

1. **A commit's diff is `git show <sha>` = commit-vs-first-parent**, already rendered today via
   `DiffSide.Commit` (`GitService.GetDiff`, `GitBench/Git/GitService.cs:2503,2514`). The
   per-increment diff needs **no new git plumbing** — only a way to (a) list the commits in a
   range and (b) drive the existing commit-details surface from that list.
2. **A second OS window is already a solved problem.** The diff pop-out (`DiffWindows`) is a
   complete, battle-tested template for "open a real native window with a custom widget tree from
   a message." We copy it wholesale (see *Grounding → Multi-window*).

## UX north star — what we're copying (and from whom)

Researched against Graphite, Sapling/**ReviewStack**, Phabricator/Differential, Gerrit, GitHub,
GitLab, and Reviewable. The desktop wins — the things a local libgit2 client can do that the
GitHub web UI can't — cluster around three ideas, which set our build priority:

**Tier 1 — the core that makes stacked review work:**
- **Per-increment diff = `commit^ → commit`, never `base → commit`.** ReviewStack's single most
  important idea: a stacked commit's branch contains *all commits below it too*, so GitHub shows a
  cumulative diff. Diffing each commit against its own parent shows *only that increment's*
  changes, free of lower-stack noise. We get this for free from `DiffSide.Commit`.
  ([sapling-stack](https://sapling-scm.com/docs/git/sapling-stack/))
- **A persistent left rail = the stack as a vertical list, base→tip, with a "you are here"
  marker.** Union of Gerrit's *Relation Chain* (arrow marks current; below = depends-on, above =
  dependents) and ReviewStack's linear ordered list. Dense rows: short-sha · summary · churn
  (`+N −M`) · per-increment reviewed/decision badge · "Increment N of M".
  ([Gerrit](https://gerrit-review.googlesource.com/Documentation/user-review-ui.html),
  [Graphite](https://graphite.com/features/pr-page))
- **Per-file "Viewed" checkbox** that collapses the file, persists, and drives an "X / Y files
  viewed" progress bar — *and auto-unchecks only the files whose content changed since you viewed
  them* ("Changed since last view" badge), keyed to a per-file content hash. The near-universal
  must-have (GitHub/GitLab/Gerrit); the auto-uncheck subtlety is what cheap clones get wrong.
  ([GitHub: Mark files as viewed](https://github.blog/news-insights/product-news/mark-files-as-viewed/))
- **Aggressive single-key keyboard nav** — desktop has real focus and no browser-shortcut
  conflicts, so use no-modifier keys for the high-frequency actions: `j`/`k` file, `]`/`[` (or
  `Shift+J/K`) increment, `v` mark-viewed, `n` next-unreviewed, `?` cheatsheet.

**Tier 2 — re-review after amends (GitBench's natural advantage):**
- **Two-sided version comparator** (Phabricator "Diff 1 vs Diff 3", Gerrit "Diff Against",
  Graphite "Base → v4", ReviewStack "{before} vs {after}"): reconstruct increment snapshots from
  reflog/force-push history; default left = `Base` (parent).
- **"Since I last reviewed" via `git range-diff`, not a naive two-commit diff** — GitHub's version
  breaks on force-push; range-diff makes a pure rebase *not* read as new edits.
  ([analysis](https://daisy.wtf/writing/github-changes-since-last-review/))

**Tier 3 — polish that differentiates:** Graphite's "upstack" gutter marker + peek; a
conversation drawer (toggle `Ctrl+.`, ReviewStack) with batched/pending comments + draft
autosave; a Reviewable-style completion donut / "what I owe" counters; Gerrit-style per-layer
status decorations (outdated / merged / abandoned).

Layout we're targeting (desktop, not a browser tab): **top range/progress bar**, then a split of
**stack rail (left)** and the reused **commit-details surface (right)** — itself a file-list +
diff split — with the conversation drawer as a later right-hand toggle.

## Grounding — what exists today

### Diff + commit data (unchanged from before)
- **Diff data model** (`Git/DiffResult.cs:3`): `DiffResult` is *one file* on one
  `DiffSide { Unstaged, Staged, Commit }`. Multi-file = a list of `FileChange`
  (`Features/Commits/CommitDetails.cs:20`), each diffed lazily.
- **Per-commit diff** is already commit-vs-first-parent: `GetDiff(repo, path, DiffSide.Commit,
  sha)` → `git show --format= -M <sha> -- <path>` (`GitService.cs:2503,2514`). The unit is
  `DiffTarget(Path, Side, CommitSha?)` (`Features/Diff/DiffViewModel.cs:12`).
- **Commit listing** walks LibGit2Sharp: `GitService.Load(repo, cap)` over
  `lg.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = …, SortBy = Topological|Time })`
  (`GitService.cs:87,220-236`). Per-commit metadata + files: `LoadDetails(repo, sha)`
  (`GitService.cs:326`).
- **Range support is absent.** No `commitA..commitB` diff. The only range-ish primitive is
  `IsAncestor` → `git merge-base --is-ancestor` (`GitService.cs:1681-1687`); a `merge-base` that
  *returns the SHA* does not exist yet.
- **The commit-details surface we reuse**: `CommitDetailsView` does
  `ctx.Require<CommitDetailsViewModel>()` (`CommitDetailsView.cs:61`) and hosts the embedded
  `DiffView` via `Provide<DiffViewModel>{ Value = vm.DiffVm }` (`CommitDetailsView.cs:112`).
  Selection → details is message-bus driven today: `CommitsViewModel` broadcasts
  `CommitSelectedMessage` (`CommitsViewModel.cs:99`); `CommitDetailsViewModel.OnCommitSelected`
  (`CommitDetailsViewModel.cs:99-114`) loads the details and drives `DiffVm`; `StartLoad`
  (`:116`) does the background `LoadDetails`.
- **Async/stale-safe loads**: `ViewModelBase.RunBackground` + generation lanes
  (`Infrastructure/ViewModelBase.cs:80,48`).

### Multi-window — the template we copy (the heart of the re-frame)
GitBench already opens **real second OS windows**. The diff pop-out is one clean consumer of a
fully generic seam; we mirror it.

- **The seam**: `ISecondaryWindowFactory.Open(in SecondaryWindowRequest)` →
  `ISecondaryWindow` (`framework/ZGF.Gui.Desktop/ISecondaryWindowFactory.cs:11,16,26`).
  `SecondaryWindowRequest = { Func<Context,View> BuildRoot, string Title, int Width, int Height }`.
  `ISecondaryWindow = { IWindow Window; event Action Closed; void Close() }`. It is registered on
  the root context (`GuiApp.cs:78`), so **any widget can `ctx.Require<ISecondaryWindowFactory>()`**.
- **Per-window wiring is done for us** (`SecondaryWindowFactory.Open`,
  `SecondaryWindowFactory.cs:39-67`): a GLFW window sharing the main GL context + font registry, a
  per-window `Canvas` + `DesktopInputSystem`, and — critically — **a child `new Context(_mainContext)`**
  (`:52`). Resolution walks the parent chain (`Context.cs:163`), so a per-window VM resolves every
  shared app singleton (`IGitService`, `IRepoRegistry`, `IMessageBus`, `State<T>`, …) transparently
  while getting its own canvas/input. The OpenGL run loop already renders + routes input to every
  visible window (`framework/ZGF.Desktop/Backends/OpenGl/OpenGlApp.cs:130`). **No framework change
  is needed.**
- **The consumer template — `DiffWindows`** (`Features/Diff/`):
  - `OpenDiffWindowMessage(DiffTarget)` (`Messages/OpenDiffWindowMessage.cs`) — broadcast to open;
    carries *pinned* data, not live state, so the window loads independently.
  - `DiffWindowsViewModel` (`DiffWindowsViewModel.cs:14`) owns an
    `ObservableList<DiffWindowViewModel> Windows` (`:23`), subscribes to the message (`:37`), and
    appends a pinned per-window VM (`:40-47`). Auto-constructed by the context — **no registration**.
  - `DiffWindowsView` (`DiffWindowsView.cs:20`) is a **zero-sized presenter** mounted once in
    `AppView` (`App/AppView.cs:43-44`); on list `Added` it calls
    `factory.Open(new SecondaryWindowRequest{ BuildRoot = c => new DiffWindowRootView{ Model = vm
    }.BuildView(c), Title, Width=900, Height=700 })` (`:85-95`), tracks the window in a dict, and
    wires `win.Closed += () => vm.Close(windowVm)` (`:98`). The `ObservableList` is the single
    source of truth; native-close → `Closed` → list `Removed` → `win.Close()` (idempotent).
  - `DiffWindowRootView` (`DiffWindowRootView.cs:17`) builds the window's widget tree and injects
    its VM via `Provide<T>`.
  - **No focus-existing/dedupe today** — every message opens a new window. We add a
    singleton-per-`(repo, head)` guard ourselves (Phase 6).

### Window vs Dialog (so we pick the right one)
- **Dialog = in-app overlay, *not* a separate OS window.** `ShowDialogMessage(Func<Context,
  Action, View>)` (`Messages/ShowDialogMessage.cs:12`) → `DialogPresenter` (`DialogPresenter.cs:7`)
  mounts into the `DialogSurface` in the *main* window's stack (`AppView.cs:42`). This is what
  "Merge X into Y…" uses (`MergeBranchDialog`, `MergeBranchDialog.cs:17`). Good for a small modal
  (e.g. the optional range picker), **not** for the review surface.
- **`DiffWindows` = a true separate OS window.** This is what the Review window is.

### Branch context-menu construction (the entry point)
- `BranchesViewModel.BuildLocalBranchMenuItems(string fullPath, bool isHead)`
  (`BranchesViewModel.cs:732`) and `BuildRemoteBranchMenuItems(remote, fullPath)` (`:885`) return
  `IReadOnlyList<RepoBarContextMenu.Item>`. Item shape:
  `new RepoBarContextMenu.Item(label, Action action, icon, Enabled: bool, …)`.
- **"Merge X into Y…" end-to-end** (`:788-797`): the item's action is
  `() => _bus.Broadcast(new ShowDialogMessage(onClose => new MergeBranchDialog{ Request = …,
  OnClose = onClose }))`. **Our menu item is identical in shape but broadcasts an
  `OpenReviewWindowMessage` instead** (handled by a window presenter, not the dialog presenter).
- Transient success/info feedback is a **toast**: `_bus.Broadcast(new ShowToastMessage(
  ToastIntent.Info(...)))` (`IToastService`/`ToastService`, `Features/Notifications/`,
  `Messages/ShowToastMessage.cs`). *(There is no `StatusFeedbackService`/`ShowFeedbackMessage` in
  this repo — earlier drafts mis-named it.)* This is what Phase 1's placeholder action used.

## Architecture (target)

```
Branch context-menu item  ── _bus.Broadcast(OpenReviewWindowMessage) ─────────────┐
  (BranchesViewModel, like the "Merge…" item)                                     │
                                                                                  ▼
ReviewWindowsViewModel : IDisposable           ← new; mirrors DiffWindowsViewModel
   • ObservableList<ReviewWindowViewModel> Windows
   • subscribes OpenReviewWindowMessage → appends a PINNED per-window VM
   • singleton guard: focus existing window for same (repoId, headRef)
        │
ReviewWindowsView : Widget  (zero-sized presenter, mounted once in AppView)        ← new
   • on Added → ISecondaryWindowFactory.Open(SecondaryWindowRequest{
   •     BuildRoot = c => new ReviewWindowRootView{ Model = vm }.BuildView(c),
   •     Title = "Review: <branch>", Width, Height })
   • tracks ISecondaryWindow per VM; win.Closed → vm.Close()
        │
        ▼   (per-window child Context — resolves all shared services)
ReviewWindowRootView : Widget                                                      ← new
   ├── top:  ReviewHeaderBar  (base ⟶ head · "Increment N of M" · X/Y viewed · range/version)
   └── body: split
        ├── left:  ReviewStackList   (the rail: increments base→tip, reviewed badges)  ← new
        └── right: CommitDetailsView (REUSED → its own file list + DiffView)
                     driven by CommitDetailsViewModel.Show(repoId, sha)             ← new method
        │
ReviewWindowViewModel : ViewModelBase<ReviewState>                                 ← new
   • holds the PINNED ReviewSession (repoId, baseRef, headRef, labels)
   • loads the stack through IReviewStackSource  ← seam: stub first, git later
   • SelectedSha, reviewed-state, nav commands; drives the right pane via Show()

IReviewStackSource                                                                 ← new seam
   • Phase 3: StubReviewStackSource     (naive HEAD~N..HEAD — exercises the whole GUI on real diffs)
   • Phase 4: GitReviewStackSource      (real base..head, first-parent, via new git reads)
```

New types (alongside `CommitGraph.cs`'s records):

```csharp
public readonly record struct OpenReviewWindowMessage(
    Guid RepoId, string HeadRef, string HeadLabel,
    string? BaseRef = null, string? BaseLabel = null);   // null base = "auto (merge-base)"

public sealed record ReviewSession(
    Guid RepoId, string HeadRef, string HeadLabel, string? BaseRef, string? BaseLabel);

public sealed record ReviewIncrement(
    string Sha, string ShortSha, string Summary, string Author,
    DateTimeOffset When, int FilesChanged, int Added, int Removed);  // churn lazy/0 in MVP

public sealed record ReviewStack(
    Guid RepoId, string BaseSha, string HeadSha,
    string BaseLabel, string HeadLabel,
    IReadOnlyList<ReviewIncrement> Increments, bool Truncated);
```

## Locked decisions

1. **Surface = a dedicated secondary OS window** (not a `MainViewMode`, not an overlay). Built by
   copying the `DiffWindows` template; no framework changes. *(This replaces the old plan's
   `MainViewMode.Review` + `State<ReviewSession?>` approach entirely.)*
2. **The session is pinned in the open-message payload**, exactly like `OpenDiffWindowMessage`
   pins a `DiffTarget`. The window does **not** track the active repo — opening a review pins it to
   that repo+range, so changing the active repo in the main window never disturbs an open review.
3. **Increment = commit.** The chain is the first-parent commit list of `base..head`, oldest→newest.
4. **Per-increment diff reuses `DiffSide.Commit`** (`commit^ → commit`). No new git diff plumbing in
   the MVP — and this is also the headline UX decision (ReviewStack's per-parent diff).
5. **A data seam (`IReviewStackSource`) decouples the GUI from the git layer** so the window can be
   built and tuned (Phase 3) before the range backend exists (Phase 4). The only swap between them
   is one DI binding.
6. **New git reads are additive**: `MergeBase(repo, a, b)` and `LoadReviewStack(repo, base, head,
   cap)`. Nothing existing changes behavior.
7. **Right pane reuses `CommitDetailsView`**, driven directly via a new public
   `CommitDetailsViewModel.Show(repoId, sha)` (extracted from `OnCommitSelected`). Because the
   window has its own VM instances in its own context, there is **no message-bus cross-talk** with
   the History pane — the old plan's concern there evaporates.
8. **Reviewed-state is per-file ("Viewed"), with increment-level progress derived from it.** MVP can
   ship increment-level reviewed-state first (Phase 5a) and add the GitHub-style per-file Viewed
   checkbox (Phase 5b); persistence is later (Phase 6 / Open decisions).

## Implementation plan — outside-in (each phase runs + is testable)

### Phase 1 — Entry point: the context-menu item  ✅ DONE

Add the affordance and prove it fires, with **no window and no git work yet**.

- **1.1** In `BranchesViewModel.BuildLocalBranchMenuItems` (`:732`) add
  `new RepoBarContextMenu.Item(s.ReviewChanges, () => StartReview(fullPath, isHead),
  LucideIcons.<icon>, Enabled: …)`. Mirror it in `BuildRemoteBranchMenuItems` (`:885`).
- **1.2** `StartReview(fullPath, isHead)` builds an `OpenReviewWindowMessage(repo.Id, headRef:
  fullPath, headLabel: shortName, BaseRef: null)` and **for this phase** broadcasts a
  `StatusFeedbackService.ShowFeedbackMessage($"Review: {baseLabel}..{headLabel}")` placeholder so
  the click is observable. (`BaseRef = null` ⇒ "auto"; real merge-base resolution lands in Phase 4.)
- **1.3** Add the `OpenReviewWindowMessage` record (`Messages/`) now — Phase 2 needs it.
- **Ship / test locally:** right-click a branch → **"Review changes…"** appears, enabled only when
  sensible (disabled on the empty/no-active-repo case); clicking flashes the status-bar feedback
  with the right branch label. Verifiable live via the GUI MCP tools (`gui_click button:right` →
  `gui_snapshot` for the menu item → click → confirm feedback).

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **`OpenReviewWindowMessage`** lives at `Messages/OpenReviewWindowMessage.cs` exactly as the
  *New types* block specifies: `readonly record struct (Guid RepoId, string HeadRef, string
  HeadLabel, string? BaseRef = null, string? BaseLabel = null)`. It compiles but is **not yet
  broadcast** (no subscriber until Phase 2).
- **`StartReview` signature is `(string headRef, string headLabel)`** — *not* the step-1.2
  `(fullPath, isHead)`. It's generic over local/remote so both menus reuse it; `isHead`/base
  resolution were unnecessary for a read-only placeholder. It currently *constructs* the message
  and then broadcasts a **toast** built from its fields. (`BranchesViewModel.cs:737`.)
- **Both menus wired:** local `BuildLocalBranchMenuItems` calls `StartReview(capturedName,
  capturedName)` (`:779`); remote `BuildRemoteBranchMenuItems` calls `StartReview(remoteRef,
  remoteRef)` where `remoteRef = "{remote}/{branch}"` (`:926`). Item sits **right after Checkout**
  in both, icon `LucideIcons.Search`, **`Enabled` unconditional** (read-only window; the
  no-active-repo case is already guarded by each builder's early `return` and by `StartReview`'s
  own `if (repo == null) return`). ⇒ today **`HeadRef == HeadLabel`** for both local and remote.
- **Feedback is a toast, not a "status-bar feedback" service.** This repo has **no
  `StatusFeedbackService` / `ShowFeedbackMessage`** (the plan/grounding mis-named it). Transient
  feedback is `_bus.Broadcast(new ShowToastMessage(ToastIntent.Info(...)))` via `IToastService`
  (`Features/Notifications/`, `Messages/ShowToastMessage.cs`). The placeholder string is a
  **hardcoded literal** (intentionally un-localized — it's deleted in 2.5).
- **Localization:** key `branches.context_review_changes` → property `s.BranchesContextReviewChanges`,
  added to **all 6** `Strings/*.json`. `using GitBench.Features.Notifications;` added to
  `BranchesViewModel` for `ToastIntent`.
- **Icon caveat:** `LucideIcons` is a *fixed glyph subset* of the bundled font — there is no
  review/pull-request glyph and one can't be added without extending the font, so `Search` (inspect)
  was reused. Swap freely if the font gains a better glyph.

### Phase 2 — Open the real (empty) window  ✅ DONE

Stand up the windowing skeleton by copying `DiffWindows`. The window opens, is titled, is movable,
closes cleanly — content is a placeholder.

**Current world (verified at Phase-1 close) — read these before starting:**
- **The 2.5 swap point already exists.** `BranchesViewModel.StartReview` (`:737`) builds the
  `OpenReviewWindowMessage` and broadcasts a placeholder toast. Step 2.5 is literally: **delete the
  toast lines, broadcast the message** — `_bus.Broadcast(new OpenReviewWindowMessage(repo.Id,
  headRef, headLabel))`. Nothing else in `BranchesViewModel` changes.
- **Mount point is `AppView.Build` (a `Build` override, not `CreateView`).** `new DiffWindowsView()`
  is the last child of the root `Stack` at **`AppView.cs:44`** (siblings: `frame`, `ToastHostView`,
  `DragOverlay`, `DialogSurface`). Add `new ReviewWindowsView()` as the next sibling there, and add
  `using GitBench.Features.Review;` to `AppView.cs`.
- **Presenter shape to mirror (`DiffWindowsView.cs:20-108`):** `internal sealed record
  ReviewWindowsView : Widget` with `protected override View CreateView(Context ctx) => new
  Core(ctx)`. The nested `Core : ContainerView` pins `Width = Height = 0`, resolves
  `ISecondaryWindowFactory` (`ctx.Require`), optional `IWindowChrome` + `State<ThemeMode>` (`ctx.Get`,
  null on Linux) for title-bar theming, `ctx.Require<ReviewWindowsViewModel>()`, calls
  `this.UseViewModel(() => vm, _ => {})`, then `this.Use(() => vm.Windows.Subscribe(OnWindowsChanged))`.
  Keep a `Dictionary<ReviewWindowViewModel, ISecondaryWindow>`. On `Added` → `factory.Open(new
  SecondaryWindowRequest { BuildRoot = c => new ReviewWindowRootView{ Model = vm }.BuildView(c),
  Title = vm.Title, Width = 1100, Height = 800 })`; then `win.Closed += () => _vm.Close(vm)`, store
  it, `ApplyTitleBarTheme(win)`. On `Removed` close the OS window; on `Cleared/Reset` close all.
- **VM auto-construction is real — no DI registration.** `DiffWindowsViewModel` is `internal sealed
  class : IDisposable`; ctor `(IRepoRegistry, IGitService, IUiDispatcher, IMessageBus,
  ILocalizationService)`; it `_bus.SubscribeScoped<OpenDiffWindowMessage>(...)` in the ctor and on
  each message `Windows.Add(new DiffWindowViewModel(...))`. `ctx.Require<DiffWindowsViewModel>()`
  constructs it on demand. Mirror exactly for `ReviewWindowsViewModel` +
  `SubscribeScoped<OpenReviewWindowMessage>`; the pinned per-window VM holds the `ReviewSession`
  built from the message payload.
- **Root view shape (`DiffWindowRootView.cs:17-50`):** `internal sealed record ReviewWindowRootView
  : Widget` with `required ReviewWindowViewModel Model { get; init; }`; `CreateView` builds the tree
  and injects the per-window VM via `new Provide<T>{ Value = …, Child = … }`. Per-window
  `InputSystem` comes from the passed `ctx` (`ctx.Require<InputSystem>()`) — it's the window's own,
  not the main window's, because `SecondaryWindowFactory` hands each window a child `Context`.
- **`SecondaryWindowRequest` fields:** `{ Func<Context,View> BuildRoot, string Title, int Width,
  int Height }`. `ISecondaryWindow = { IWindow Window; event Action Closed; void Close() }`. (No
  framework change needed; confirmed unchanged.)

- **2.1** `ReviewWindowViewModel` — holds the pinned `ReviewSession` + `Title`. Minimal for now.
- **2.2** `ReviewWindowsViewModel : IDisposable` with `ObservableList<ReviewWindowViewModel>
  Windows`; ctor injects `IRepoRegistry, IGitService, IUiDispatcher, IMessageBus,
  ILocalizationService` (auto-constructed, no registration); subscribes to `OpenReviewWindowMessage`
  and appends a pinned VM. Mirror `DiffWindowsViewModel.cs:14,37,40`.
- **2.3** `ReviewWindowsView : Widget` — zero-sized presenter; on list `Added` →
  `ISecondaryWindowFactory.Open(...)` with `BuildRoot = c => new ReviewWindowRootView{ Model = vm
  }.BuildView(c)`, `Title = "Review: <headLabel>"`, e.g. `Width = 1100, Height = 800`; track per
  VM; wire `Closed`; apply title-bar theme. **Mount it once in `AppView` next to `new
  DiffWindowsView()`** (`AppView.cs:43-44`). Mirror `DiffWindowsView.cs:85-100`.
- **2.4** `ReviewWindowRootView : Widget` — placeholder tree ("Review: `<base>` ⟶ `<head>`")
  resolving its per-window `InputSystem`/services from the passed `ctx`, VM injected via
  `Provide<ReviewWindowViewModel>`. Mirror `DiffWindowRootView.cs:17`.
- **2.5** Replace Phase 1's status-bar placeholder: `StartReview` now just broadcasts the message;
  the presenter opens the window.
- **Ship / test locally:** right-click a branch → "Review changes…" → a **real separate OS window**
  opens, titled with the branch, showing the placeholder. Close (native title bar + programmatic)
  works; reopening works; opening two reviews yields two windows.

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **New `Features/Review/` package** with five files: `ReviewStack.cs` (records),
  `ReviewWindowViewModel.cs`, `ReviewWindowsViewModel.cs`, `ReviewWindowsView.cs`,
  `ReviewWindowRootView.cs`. `AppView.cs` mounts `new ReviewWindowsView()` right after
  `new DiffWindowsView()` (`+ using GitBench.Features.Review;`).
- **`ReviewStack.cs` carries all three records now** (`ReviewSession`, `ReviewIncrement`,
  `ReviewStack`) per the *New types* block, `public sealed record` (matching `DiffTarget`/`CommitNode`
  domain-record convention), even though Phase 2 only consumes `ReviewSession`. `ReviewIncrement`/
  `ReviewStack` are defined-but-unused until Phase 3/4 — intentional, no warning.
- **`ReviewWindowViewModel` derives its own title** (`Title = $"Review: {session.HeadLabel}"`) from
  the pinned `ReviewSession` in its ctor — the windows-VM doesn't compute/pass it. Still `internal
  sealed class : IDisposable` with a no-op `Dispose()` for now.
- **`ReviewWindowsViewModel` ctor injects only `IMessageBus`** — *not* the step-2.2 five-arg mirror
  of `DiffWindowsViewModel`. Phase 2 genuinely uses only the bus (it appends a bare
  `ReviewWindowViewModel(session)`); the four extra deps would be assigned-but-unread, and Phase 3's
  real deps (`IReviewStackSource`, the details VM) aren't the same set anyway — so mirroring them now
  buys nothing. Auto-construction resolves the single-arg ctor fine; Phase 3 just widens it.
- **`ReviewWindowRootView` uses the high-level `Build`/`IWidget` style** (`Box` + `Center` + `Text` +
  `Provide<ReviewWindowViewModel>`, à la `AboutDialog`), *not* the low-level `CreateView`/`View` style
  of `DiffWindowRootView` — the placeholder takes no input, so no `InputSystem`/controller wiring is
  needed yet. Background `Theme.Color(s => s.Palette.Surface)`, muted `TextSecondary` label. It still
  `Provide`s its VM into the subtree so Phase 3 can resolve it. Uses a plain `→` (U+2192), not the
  plan's `⟶` (U+27F6), to stay within bundled-font glyph coverage.
- **Window title is a hardcoded `"Review: <headLabel>"`** (un-localized) — same precedent as the diff
  pop-out's raw file-path title; the `SecondaryWindowRequest.Title` plain string never touches the
  source-gen `Strings` flow, so no LOC004. Phase 6 localizes the header/title strings.
- **`StartReview` (`BranchesViewModel`) now just `_bus.Broadcast(new OpenReviewWindowMessage(repo.Id,
  headRef, headLabel, BaseRef: null))`** — the Phase-1 toast lines are gone, and the now-unused
  `using GitBench.Features.Notifications;` was removed. `Width = 1100, Height = 800`.

### Phase 3 — Window GUI (driven by stub data)

Build the full review layout and interactions against an `IReviewStackSource` stub, so the UI is
tunable before the range backend exists. Use a stub that returns **real commits** (`HEAD~N..HEAD`,
first-parent) so the file list + diff render real content end-to-end — only the *range semantics*
are still fake.

- **3.1** Define the seam `IReviewStackSource { Task<ReviewStack> LoadAsync(ReviewSession session,
  int cap); }` and `StubReviewStackSource` (naive HEAD-relative walk; ignores `BaseRef`).
- **3.2** Extract `public void Show(Guid repoId, string sha)` / `public void Clear()` from
  `CommitDetailsViewModel.OnCommitSelected` (`CommitDetailsViewModel.cs:99-114`); `OnCommitSelected`
  forwards to them. Zero behavior change for the History pane.
- **3.3** `ReviewWindowViewModel : ViewModelBase<ReviewState>` —
  `RunBackground(() => _source.LoadAsync(session, cap))`, store the `ReviewStack`, auto-select the
  first increment; expose `SelectIncrement(sha)` driving the right pane via `_details.Show(repoId,
  sha)`; expose `(int reviewed, int total)` for the header (stubbed reviewed=0 for now).

  ```csharp
  internal sealed record ReviewState(
      ReviewRenderState Render,          // Placeholder / Loading / Loaded(ReviewStack)
      string? SelectedSha,
      IReadOnlySet<string> ReviewedShas);
  ```
- **3.4** Build the real tree in `ReviewWindowRootView`:
  - **`ReviewHeaderBar`** — `base ⟶ head`, "Increment N of M", "X / Y reviewed" progress, and a
    placeholder slot for the later range/version comparator.
  - **Body split** (copy `HistoryView`'s draggable divider, `CommitHistory.cs:23-141`):
    **left** = `ReviewStackList` (one dense row per `ReviewIncrement`: reviewed check · short-sha ·
    summary · churn · author/date; "you are here" highlight; clicking → `SelectIncrement`), reusing
    the reactive list widgets (`Column<T>`/`Each<T>`, per project convention) — **not** the heavy
    `CommitsView.Core` graph renderer (a linear stack needs no lanes); **right** = the reused
    `CommitDetailsView`, with the window's dedicated `CommitDetailsViewModel` provided into its
    sub-context.
- **3.5** Loading + empty states: skeleton while `LoadAsync` runs (reuse the Pulse/Skeleton kit);
  empty-range message when the stack has 0 increments.
- **Ship / test locally:** open the window → full 3-region GUI; the rail lists increments; clicking
  one updates the file list + diff (with syntax highlighting + intra-line + hunk actions, all
  inherited from `DiffView`); divider drags; layout/spacing/styling tunable. All on real diffs.

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **New `Features/Review/` files:** `IReviewStackSource.cs`, `StubReviewStackSource.cs`,
  `ReviewHeaderBar.cs`, `ReviewStackList.cs` (carries `ReviewStackList`, `ReviewStackRow`, and
  `ReviewRowController`). `ReviewWindowViewModel` promoted to `ViewModelBase<ReviewState>`;
  `ReviewWindowsViewModel` ctor widened; `ReviewWindowRootView` rebuilt into the real tree.
  `CommitDetailsViewModel` gained `Show`/`Clear` + a `subscribeToSelection` flag. The stub is bound
  in `AppServices` (`AddSingleton<IReviewStackSource, StubReviewStackSource>()`).
- **Seam shape:** `IReviewStackSource.LoadAsync` returns `Task<ReviewStack>` (no `Fetched`/error
  field as in step 3.1); an empty range is an empty `Increments` list and a load failure is a thrown
  exception. The VM bridges it through the proven `RunBackground` path via
  `LoadAsync(...).GetAwaiter().GetResult()` on the worker (the stub completes synchronously — a truly
  async Phase-4 source can move off this bridge).
- **`CommitDetailsViewModel` extraction (3.2):** `Show(repoId, sha)` now resolves the repo **by id**
  (active-first, then `_registry.Repos`) so a pinned surface keeps working off the active repo;
  `OnCommitSelected` keeps the active-repo guard before forwarding, so the History pane's behavior is
  unchanged. **Caveat (decision #2 not fully met yet):** the embedded `DiffViewModel` still resolves
  `_registry.Active.Value` for its own diff load, so a review window's diffs are correct only while
  the reviewed repo is the active one — the Phase-3/stub case. Threading the pinned repo through
  `DiffViewModel` is a later refactor.
- **Cross-talk fix is the `subscribeToSelection: false` flag, not child-context isolation.** The plan
  (decision #7) assumed the per-window context made cross-talk "evaporate"; it doesn't, because the
  message bus is a shared singleton, so a second `CommitDetailsViewModel` would still receive the
  History pane's `CommitSelectedMessage`. The window's instance opts out of that subscription and is
  driven only via `Show()`.
- **Draggable divider via reuse, not a copy of `HistoryView`.** The body is a `BorderLayout` with
  `West = ResizableSidebar { Content = ReviewStackList }` (the existing `ResizableLeftSidebar`
  draggable-edge control) and `Center =` the reused `CommitDetailsView` (wrapped by a tiny
  `CommitDetailsHost : IWidget` so it builds against the `Provide<CommitDetailsViewModel>` scope).
- **Rail rows** are high-level `Box`/`Row`/`Column` widgets (`Column<ReviewIncrement>` over
  `vm.Increments`), using the shared `RowSelectionStyles` token (`Fill`/`FillHover`/`AccentBar`) for
  the selected/hovered look — a **snap**, not the animated slide (the rail reads like a commit list).
  Reviewed-state is a hollow-circle placeholder (stubbed unreviewed); churn is hidden while `+0 −0`
  (the stub leaves churn at 0). No `CommitsView.Core` graph renderer, per the plan.
- **Header/empty/loading strings are hardcoded English literals** (Phase 1/2 precedent); localization
  is deferred to Phase 6, so no `Strings/*.json` / LOC004 churn here. The loading state is a
  centered "Loading…" with a `FadeIn` bloom rather than a bespoke skeleton — the right pane already
  shows the real `CommitDetailsSkeleton` while the first commit loads, and the stub load is
  effectively instant.
- **Stub semantics:** ignores `BaseRef`, walks the **first-parent chain from the current HEAD**
  through the existing `IGitService.Load(...)` snapshot (`FakeRangeSize = 12`), reversed to
  oldest→newest; base = the oldest increment's first parent. So the *range is fake* (reviewing a
  non-checked-out branch still walks HEAD; the header may show the clicked branch name over HEAD's
  commits) — exactly the Phase-3 contract. The VM auto-selects `Increments[0]` (base-most) ⇒
  "Increment 1 of M".

### Phase 3.5 — Tabbed commit-details surface (interjected after Phase 3)  ✅ DONE

Not in the original plan. While tuning Phase 3 it became clear the reused `CommitDetailsView` squashed
everything: selecting a file opened the diff in a third stacked pane that compressed the metadata +
file list above it. Reworked the **shared** `CommitDetailsView` (so this lands in *both* the History
pane and the Review window) into a file list + tabbed metadata/diff surface. No git or
`IReviewStackSource` changes — purely the right-pane presentation.

**Final layout** — a single draggable `VerticalSplitContainer`:
- **Top pane: the file list (`FileChangesSection`), always visible.** Pulled out of the old inner
  split so it never disappears when a diff is open.
- **Bottom pane (default 2/3): a tabbed metadata/diff region.** A `CommitDetailsTabStrip` over a body
  that swaps between the **Details** tab (commit metadata) and one tab per open file (its diff). The
  strip stays attached to this region (it's the `North` of the bottom pane).
- Clicking a file in the top list opens (or focuses) its tab below; the list stays put. Selecting a
  different commit closes all file tabs and returns to **Details**.

**Key implementation points:**
- **One `DiffViewModel` per open file tab** (`CommitFileTab`, each pinned to a `DiffTarget(path,
  DiffSide.Commit, sha)`). Switching tabs **toggles `View.IsVisible`** on stacked bodies inside a
  `FlexColumnView` — it never unmounts a body, so each diff keeps its scroll, highlight, and full-file
  toggle. (Detaching would dispose the diff's factory controllers; `IsVisible` = `display:none` keeps
  them mounted.) Stale-while-revalidate still holds: tabs are always cleared before any
  placeholder/skeleton, so a diff body never reattaches.
- `CommitDetailsViewModel` gained `ObservableList<CommitFileTab> OpenTabs`, `SelectFile`/`ActivateTab`/
  `CloseTab`/`ActiveDiff`; `Show`/`Clear`/`Dispose` reset+dispose tabs. The old single `DiffVm` /
  `SelectedTarget` are gone (`SelectedPath` now = active tab; null = Details). Arrow / `j`-`k` over the
  always-visible list opens the next file's tab, preserving sequential review.
- **Tab strip** reuses the Actions toolbar's scrollbar-less `HorizontalScrollArea` (wheel — vertical
  wheel included — pans it; clips on overflow). Tabs size to content, capped at 220px via `MaxWidth`
  on the pill + label in a `Grow` + ellipsis (a flex measures intrinsic width *unclamped* but lays out
  *clamped*, so a capped label alone left dead space — the `Grow` makes the cap flow into the label).
  1px right-border dividers; active/hover use the shared `RowSelection` token.
- `DiffPaneHeaderWidget` gained a `Collapsible` flag (default true; **false** in tabs — the diff fills
  its tab, so the collapse chevron is dropped). Local Changes is untouched (still uses the stacked
  split; out of scope).
- **New files:** `Features/Commits/CommitFileTab.cs`, `Features/Commits/CommitDetailsTabStrip.cs`
  (strip + `CommitDetailsTab` + `CommitFileTabButton` + shared `CommitTabChrome` + `TabClickController`).
- **Localization:** `commits.details_tab` ("Details") added to all 6 `Strings/*.json`; the close button
  reuses `common.close`.

**Note for Phase 5b:** the per-file "Viewed" checkbox now lands on the tab strip / per-tab diff rather
than on a row in a stacked list — design the `IReviewedFileTracker?` seam accordingly.

### Phase 4 — Real backend (the git range layer)  ✅ DONE

Swap the stub for the correct `base..head` range. The GUI does not change — only the source.

- **4.1** `string? MergeBase(Repo repo, string a, string b)` → `git merge-base a b` via `RunGit`;
  trim; null on failure. Mirrors `IsAncestor` (`GitService.cs:1681`). Used to resolve the default
  base when `BaseRef` is null.
- **4.2** `Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseSha, string headSha, int cap)`.
  Mirror `Load` (`GitService.cs:87,220-236`) with a range filter:

  ```csharp
  using var lg = new Repository(repo.Path);
  var filter = new CommitFilter
  {
      IncludeReachableFrom = headSha,
      ExcludeReachableFrom = baseSha,                  // ← "base..head"
      FirstParentOnly = true,                          // linear stack (decision #3)
      SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
  };
  ```

  Collect up to `cap`, map to `ReviewIncrement`, **reverse** to oldest→newest. `Added`/`Removed`
  churn can be filled lazily (`diff-tree --numstat`) in a polish pass. Resolve labels from the
  caller (branch names / short SHAs).
- **4.3** `GitReviewStackSource : IReviewStackSource` implements `LoadAsync` using `MergeBase`
  (when `BaseRef == null`, default base = `MergeBase(head, defaultBranch-or-upstream)`) +
  `LoadReviewStack`. Bind it in place of `StubReviewStackSource` (the one DI swap). Both git methods
  are additive; no existing call site changes.
- **4.4** Tests in `GitBench.Tests` (precedent: `HunkPatchBuilderTests`): empty range, single
  commit, linear stack, range containing a merge commit (first-parent linearization), `base == head`
  (empty), bad refs (Failed); plus `MergeBase` happy/again-bad-ref paths.
- **Ship / test locally:** "Review changes…" on a real feature branch → the rail shows the *correct*
  `base..head` increments; each shows commit-vs-parent files + diff. Reordering/rebasing the branch
  and reopening reflects reality.

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **Three additive git reads, not two.** `MergeBase` and `LoadReviewStack` per the plan, plus a third —
  `ResolveAutoReviewBase(repo, headRef)` — that the plan implied but didn't name in decision #6. The
  auto-base policy (open decision #3: *upstream → default → dialog*) needs git-CLI access to
  `<branch>@{upstream}` and `origin/HEAD`, which belongs in the git layer, so it lives there behind one
  public method (private `GetUpstreamRef` / `GetDefaultBranchRef` helpers; the *dialog* fallback is
  Phase 7, so unresolved ⇒ null ⇒ the source throws a friendly "couldn't determine a base"). All three
  are additive; **no existing behavior changes** (decision #6 honored in spirit).
- **`LoadReviewStack(Repo, baseRef, headRef, cap)` → `Fetched<ReviewStack>`**, mirroring `Load` on
  LibGit2Sharp: `CommitFilter { IncludeReachableFrom = headCommit, ExcludeReachableFrom = baseCommit,
  FirstParentOnly = true, SortBy = Topological | Time }`, collect up to `cap`, **reverse** to
  oldest→newest. `FirstParentOnly` exists in LibGit2Sharp 0.31.0 (build-confirmed). `base`/`head` accept
  **any ref or SHA** (resolved via `lg.Lookup<Commit>`; unresolvable ⇒ `Failed`). Labels default to
  short SHAs (the source overrides them). Churn left at 0. **Truncation drops the oldest (base-most)
  increments, keeping the newest `cap`** — the stack still reads base→tip from where it was cut.
- **`MergeBase(Repo, a, b)` via `RunGit`** (CLI, like the grounding's `IsAncestor` but returning the
  SHA): null on any non-zero exit — `RunGit` already maps unrelated histories (exit 1) and bad refs
  (exit 128) to null, so one null-check covers both.
- **`GitReviewStackSource`** (new) resolves the base — explicit `BaseRef` ⇒ `MergeBase(BaseRef, head)`
  (falling back to the ref itself); null `BaseRef` ⇒ `ResolveAutoReviewBase` — then `LoadReviewStack`,
  then overrides `HeadLabel = session.HeadLabel`, `BaseLabel = session.BaseLabel ?? short-sha(base)`.
  Since Phase 1/2 set `HeadRef == HeadLabel ==` branch name and `BaseRef = null`, **every review today
  auto-resolves its base**; the explicit-base path is dead until Phase 7's range dialog feeds it.
- **Seam contract kept (not the plan's `Fetched` return on the seam):** `IReviewStackSource.LoadAsync`
  still returns `Task<ReviewStack>`; the source **throws** on any failure (repo gone, no base, `Failed`
  fetch). The VM's existing `RunBackground` bridge (`LoadAsync(...).GetAwaiter().GetResult()`) catches
  the throw and renders it as the error placeholder — so **the GUI and the VM are unchanged**, exactly
  as the phase promised. The source returns synchronous work wrapped in `Task.FromResult`.
- **DI swap is the one line:** `AppServices` now binds `GitReviewStackSource`. `StubReviewStackSource`
  is **kept** as the Phase-3 reference impl behind the seam (still compiles; no longer bound).
- **Tests — `GitBench.Tests/ReviewStackTests.cs` (11, all green).** First tests in the suite to drive
  **real git**: each builds a throwaway repo via the `git` CLI (`Process`), then calls the real
  `GitService`. Covers linear-stack ordering + base/head SHAs, single commit, `base==head` (empty),
  cap truncation (keeps newest + `Truncated`), **merge-commit first-parent linearization** (merged side
  excluded), bad ref ⇒ `Failed`; `MergeBase` happy / bad-ref / unrelated-histories (orphan branch);
  `ResolveAutoReviewBase` default-branch fallback + no-default ⇒ null. *(Note: 12 pre-existing
  `GitIdentityServiceTests` failures on this dev machine are environmental — they read remotes from temp
  repos and resolve `NoRemote`; identical 12 fail on the clean baseline, unrelated to Phase 4.)*
- **Still open (carried from Phase 3, not a Phase-4 concern):** the embedded `DiffViewModel` resolves
  `_registry.Active.Value` for its own diff load, so a review window's *diffs* are correct only while the
  reviewed repo is the active one. Phase 4 fixed the *stack source*, not the diff pane — threading the
  pinned repo through `DiffViewModel` remains a later refactor (decision #2 still partial).

### Phase 5 — Reviewed-state, progress, next-unreviewed  ✅ DONE

Make it a review tool, not just a stack viewer.

- **5a (increment-level, ephemeral):** a `HashSet<string> ReviewedShas` on `ReviewWindowViewModel`;
  `ToggleReviewed(sha)`, `MarkReviewedAndAdvance()`, `NextUnreviewed()`; the rail shows a check and
  the header shows "N / M increments reviewed". Reset on range change / window close.
- **5b (per-file "Viewed" — the headline feature):** a GitHub-style Viewed checkbox per file that
  collapses the file and feeds an "X / Y files viewed" bar; an increment counts as reviewed when all
  its files are viewed. Key viewed-state to a **per-file content hash** so amending an increment
  auto-unchecks *only* the changed files and shows a "Changed since last view" badge.
  - **Design note (don't pollute History):** the Viewed checkbox must not leak into the History
    pane's reuse of `CommitDetailsView`. Drive it via an **optional context service** (e.g.
    `IReviewedFileTracker?`) that the file-list rows consult — present only in the Review window's
    context, absent (⇒ no checkbox) elsewhere. If that proves awkward to bolt onto the shared view,
    the fallback is a dedicated review file-list widget reusing only `DiffView` (see Open decisions).
- **5c:** `n` jumps to the next unreviewed file/increment; wire reviewed decorations into the rail
  (Gerrit-style status icons).
- **Ship / test locally:** step through a branch, tick files Viewed → progress climbs, files
  collapse; `next-unreviewed` lands correctly; amend a commit, reopen → only the changed file
  un-ticks with the badge.

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **5a (increment-level) — as specified.** `ReviewState` already carried `IReadOnlySet<string>
  ReviewedShas` from Phase 3; the VM now exposes it (`ReviewedShas`) and adds `ToggleReviewed(sha)`,
  `MarkReviewedAndAdvance()`, `NextUnreviewed()` (increment-level; wraps past the tip). The rail's
  leading indicator (`ReviewStackRow`) became a **click target**: a hollow `TextMuted` ring while
  unreviewed, a filled `Status.Success` dot once marked. A `ReviewToggleController` on the indicator's
  (padded) hit box consumes the click on bubbling so it toggles reviewed *without* also selecting the
  row — same deeper-consumes-first pattern as the tab close button. Header still shows
  `"{n} / {m} reviewed"`. (No "reset on range change" code: a window is pinned to one range for life,
  so the only reset is disposal on close.)
- **5b placement = the per-tab diff header, NOT the file-list row** — following the **Phase 3.5 note**
  (the file list is now the always-visible top pane), not the original 5b "file-list rows consult"
  text. Decisive reason found while implementing: `FileChangesSection` is a **virtualized,
  canvas-drawn** list (`VirtualRowListView` + `FileChangesUI.DrawFileRow`), so a per-row checkbox =
  manual draw + hit-test in shared canvas code — exactly the "awkward to graft onto the shared view"
  path Open-decision #1 said to avoid. The GitHub-style **"Viewed" toggle (icon + label) lives on the
  shared `DiffPaneHeaderWidget`** (where each file's diff already lives, in its tab), tinting
  `Status.Success` and flipping `Square`→`CheckSquare` once marked.
- **The seam is `IReviewedFileTracker`, placed in `Features/Diff`** (the lowest common consumer — the
  header), implemented by `ReviewedFileTracker` in `Features/Review`, to avoid a `Diff → Review`
  dependency. The header resolves it via **`ctx.Get<IReviewedFileTracker>()` (optional)** — non-null
  only in a review window (provided once at `ReviewWindowRootView`'s root via
  `Provide<IReviewedFileTracker>`), so the History pane and Local Changes show no Viewed toggle. The
  tracker keys viewed state by `(sha → set<path>)` and bumps a `Revision` counter so the header's
  auto-tracked `Prop.Bind`/`Theme.Color` computes refresh on toggle. `DiffViewModel` gained a public
  `Target` (the pinned `DiffTarget`) so the header reads the `(sha, path)` to key on.
- **Progress + auto-mark in the VM.** `ReviewWindowViewModel` **owns** the tracker
  (`ReviewedFiles`, disposed in a new `Dispose` override that leaves `_details` alone). It exposes
  `FilesViewedLabel` (a `Derived<string>` over own state + `_details.RenderState` + tracker `Revision`)
  → `"X / Y files viewed"` for the selected increment, shown in the header (hidden when empty).
  Viewing the **last** file of the selected increment auto-marks that increment reviewed
  (`MarkIncrementIfAllFilesViewed`, subscribed to tracker + details) — **one-way**: un-viewing a file
  doesn't revoke the mark (which can also be set by hand via the rail dot), so the derived and manual
  paths don't fight.
- **5c:** the rail "reviewed decoration" is the filled dot (done in 5a); `NextUnreviewed()` exists as a
  VM method. The actual **key bindings (`n`, `v`, `j`/`k`, …) are deferred to Phase 6**, which already
  owns the full keyboard set — the plan's 5c and Phase 6 overlap, so the keys land once in Phase 6.
- **Deferred to Phase 6 (carried, not built here):** the **content-hash auto-uncheck** + "Changed
  since last view" badge and **persistence** — both only bite across a reload/reopen (an amend), which
  Phase 6 owns (`RefsChanged`/`WorkingTreeChanged` reload + the optional JSON store). MVP viewed-state
  is **ephemeral per Open-decision #5**; within one window session content never changes, so
  auto-uncheck is moot until then. Per-increment file *counts* for **un-opened** increments still
  aren't available (churn/`FilesChanged` left at 0 since Phase 4), so all-files-viewed is evaluated
  only for the increment whose details are loaded.
- **Localization deferred to Phase 6** (Phases 1–3 precedent): the new strings — `"Viewed"`,
  `"X / Y files viewed"`, `"N / M reviewed"` — are hardcoded English literals (the `"Viewed"` label
  sits in shared `DiffPaneHeaderWidget` but only renders in the review context). No `Strings/*.json`
  churn, no LOC004.
- **New files:** `Features/Diff/IReviewedFileTracker.cs`, `Features/Review/ReviewedFileTracker.cs`.
  **Edited:** `ReviewWindowViewModel` (tracker + labels + nav + Dispose), `ReviewStackList`
  (indicator toggle + `ReviewToggleController`), `ReviewHeaderBar` (files-viewed label),
  `ReviewWindowRootView` (`Provide<IReviewedFileTracker>`), `DiffViewModel` (`Target`),
  `DiffPaneHeaderWidget` (gated Viewed toggle).

### Phase 5.5 — Close the review loop (feedback + progression)  [interjected after Phase 5]

Not in the original plan. Phase 5 wired the reviewed/viewed **state** but never built the surface
that exposes it or the controls that drive through it — so in practice the window reads as a stack
*viewer*, not a review *tool*: ticking **Viewed** has no effect the eye can find, and there is no
**Next**. This phase closes the loop. It is mostly *wiring methods that already exist* + *drawing
state that already exists* — small code, high payoff. No git / `IReviewStackSource` changes.

**Why "Viewed does nothing" — verified, not a bug (read before starting):**
- The state machine is correct end-to-end. Click → `Command` → `IReviewedFileTracker.ToggleViewed`
  (`ReviewedFileTracker.cs:24-30`) flips the set + bumps `Revision`; the `Derived` files-viewed
  label recomputes; viewing an increment's last file auto-marks it (`ReviewWindowViewModel.cs:236-247`).
  Nothing is mis-fired.
- The problem is purely **where feedback surfaces**: the only visible change is the header button
  glyph (`Square`→`CheckSquare` + green, `DiffPaneHeaderWidget.cs:147-160`). The **Changes file-list
  row shows nothing** (`CommitDetailsView.cs:94` only selects), the **file tab shows nothing**
  (`CommitDetailsTabStrip.cs:108-160`), and the one progress readout is a `Caption`/`TextMuted`
  string in the far top-right (`ReviewHeaderBar.cs:59-66`) — away from the diff the eye is on.
- And the loop **dead-ends**: `MarkReviewedAndAdvance()` / `NextUnreviewed()` exist
  (`ReviewWindowViewModel.cs:129,143`) but **have no callers** — no button, no key. `ReviewHeaderBar`
  is text-only; the only nav is a rail-row click (`ReviewStackList.cs:45`). Marking Viewed leaves the
  cursor exactly where it was.

**UX north star for this phase (from the research):** across GitHub, Gerrit, Graphite, and
Reviewable two patterns are universal — (1) marking a file viewed produces an **immediate, *local*,
visible change** (the file gets out of your way), and (2) there is **always an explicit advance**
(a Next control, a shortcut, or auto-advance). We have neither; 5.5 adds both.

- **5.5.1 — Viewed visibility in the always-visible Changes list (highest leverage).** The top pane
  from Phase 3.5 is our GitHub-style "what's left" surface and today it's blank. Draw a check glyph
  + dim the label on viewed rows in `FileChangesUI.DrawFileRow`. **Draw-only — no hit-test**, so it
  sidesteps the Phase 5b objection (the list is virtualized/canvas-drawn; an *interactive* checkbox
  there is painful) — the interactive toggle stays on the diff header; the row only *reflects*
  `IReviewedFileTracker.IsViewed(sha, path)`. Gate on the optional `ctx.Get<IReviewedFileTracker>()`
  + the current sha (`CommitDetailsViewModel._currentSha`) so the **History pane stays clean** (no
  tracker ⇒ no marks) — same opt-in seam as Phase 5b's header.
- **5.5.2 — Viewed mark on the file tab.** A check glyph (or strikethrough) in `CommitDetailsTabStrip`
  / `CommitTabChrome`, same optional-tracker + sha gating, so an open tab also shows done-ness.
- **5.5.3 — Header progress, promoted from caption to a real meter.** Replace the muted "X / Y files
  viewed" string with a glanceable progress bar (files within the selected increment **and**
  increments within the stack — the Reviewable "donut" idea). Reuses the data already computed for
  `FilesViewedLabel` + `ReviewedShas`/`Increments.Count`; add a **"Review complete ✓"** terminal
  state when all increments are reviewed.
- **5.5.4 — Increment nav cluster + adaptive primary action (the literal "Next").** In
  `ReviewHeaderBar`: `‹ Prev · Increment N of M · Next ›` buttons → `SelectIncrement(prev/next sha)`
  (turns today's dead "Increment N of M" text into controls). Plus a **primary action button by the
  diff** whose label adapts: *"Mark viewed → next file"* within an increment → *"Next increment ›"*
  once the increment's last file is viewed → *"Review complete"* at the tip. It wires the
  **already-existing, currently-dead** `MarkReviewedAndAdvance()` / `NextUnreviewed()`.
- **5.5.5 — Advance-on-view, stopping at the increment edge (locked).** Marking a file Viewed opens
  the next **unviewed file's** tab in the same increment (`_details.SelectFile`, the tabbed analog of
  GitHub's collapse-and-move-on). When the **last** file of the increment is viewed, the cursor
  **stays put** and the primary action flips to *"Next increment ›"* — crossing a commit boundary is
  always an explicit choice, never automatic. (New small VM method, e.g. `AdvanceToNextUnviewedFile`.)
- **5.5.6 — Rail polish.** Strengthen the "you are here" treatment; keep the **binary** reviewed dot
  (empty / filled-`Status.Success`). *Defer per-row partial progress ("2/4")* — it needs each
  increment's file count, which isn't loaded for un-opened increments (`FilesChanged` is still 0 since
  Phase 4); fold it into the later churn/`numstat` pass rather than block 5.5.
- **5.5.7 — Core keyboard (the loop-critical subset; full set still Phase 6).** Wire into the window's
  own `InputSystem`, reusing `ListArrowKbmController` where the file/increment lists already fit:
  `Enter`/`Space` = **mark viewed → advance** (5.5.5), `[`/`]` = **prev/next increment**, `j`/`k` =
  **prev/next file**. The remaining keys (`v`, `n`), the `?` **cheatsheet overlay**, and
  **localization** of all 5.5 strings stay in Phase 6 (single keyboard + localization pass there).

**Locked decisions for this phase:**
- **Auto-advance = next unviewed *file*, stop at the increment edge** (explicit hop between commits) —
  not fully-automatic across increments, and not badge-only. (User decision.)
- **Core keys land here**; `v`/`n`/cheatsheet/localization defer to Phase 6. (User decision.)
- **Viewed indicators are draw-only and gated on the optional `IReviewedFileTracker`** — History pane
  and Local Changes show nothing (same opt-in seam as Phase 5b).
- **New strings stay English literals** (Phases 1–5 precedent); localized in Phase 6 — no
  `Strings/*.json` / LOC004 churn here.
- **State stays ephemeral** (persistence + content-hash "Changed since last view" remain Phase 6).

- **Ship / test locally:** open a multi-commit branch's review. Scan the Changes list — viewed rows
  are checked/dimmed. Click a file → read → **Viewed** (row **and** tab check off, the header meter
  climbs) → the next unviewed file opens automatically → after the increment's last file the cursor
  holds and the primary action reads *"Next increment ›"* → click it (or `]`) → the rail advances and
  the dot for the finished increment is filled → at the tip the header shows **"Review complete."**
  `Enter`/`Space`, `[`/`]`, `j`/`k` drive the same loop without the mouse.

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **5.5.1 (Changes-list marks) — as specified, draw-only + gated.** `FileChangesUI.DrawFileRow` gained
  `reserveViewedColumn` / `isViewed` / `viewedIconStyle`: it reserves a fixed trailing column (so text
  width is stable whether or not a row is ticked), dims a viewed row's label to half alpha, and draws a
  success-tinted `CheckSquare` in the column. `FileChangesSection` resolves the optional
  `ctx.Get<IReviewedFileTracker>()`, holds the current sha via a new `SetReviewSha(sha)` (driven from
  `CommitDetailsView.ShowDetails`/`ShowPlaceholder` off `d.Sha`), repaints on the tracker's `Revision`,
  and passes `reviewMode`/`isViewed` per row. No tracker (History pane, Local Changes) ⇒ no column, no
  marks — same opt-in seam as Phase 5b. **No hit-test added** (the toggle stays on the diff header).
- **5.5.2 (tab mark) — as specified.** `CommitFileTab` exposes its `Sha`; `CommitTabChrome` gained an
  optional `Viewed` predicate that, when set, prepends a `Visible`-bound success `CheckSquare` before the
  label. `CommitFileTabButton` supplies it only when a tracker is in scope (reading `Revision` so it
  refreshes on toggle); the Details tab and History pane pass none.
- **5.5.3 (header meter) — split across two bars, deliberately.** The plan put *both* the files meter and
  the increments meter in the header; instead the **header carries the macro meter** (increments reviewed
  across the stack, accent-filled, with the "N / M reviewed" caption and a `CircleCheck` **"Review
  complete"** badge once `ReviewedShas.Count >= Increments.Count`) and the **action bar carries the micro
  meter** (files viewed within the selected increment, success-filled) right next to the primary action it
  describes. New `ReviewProgressMeter` widget (a rounded track + a left-anchored fill `Box` sized to a
  `0..1` fraction via a `Row`, not a stretched child). This keeps the 40px header uncluttered and groups
  each meter with the thing it measures.
- **5.5.4 (nav cluster + primary action).** The header's `Increment N of M` is now `‹ N of M ›` — bare
  `ButtonWidget`s gated by new VM `CanSelectPrev`/`CanSelectNext` slices, calling
  `SelectPrevIncrement`/`SelectNextIncrement` (clamped, no wrap). The **adaptive primary action lives in a
  new `ReviewActionBar` pinned beneath the diff** (the `Center` of `Split()` became a `BorderLayout` with
  `South = ReviewActionBar`), *not* in the header — "by the diff" per the plan, and isolated to the review
  window (never touches the shared `CommitDetailsView`). Its label/icon adapt off a single
  `Derived<ReviewHud>`: *"Mark viewed → next file"* (or *"Review files"* when on the Details tab) →
  *"Next increment"* → *"Review complete"* (disabled, gated by `PrimaryActionEnabled`).
- **5.5.5 (advance-on-view) — routed through the explicit primary action, not the header toggle.** The
  diff-header **Viewed** toggle stays a pure toggle (it can un-view), so it is **not** coupled to the
  review VM. The advance is the explicit control the north-star asks for: the action-bar button and
  `Enter`/`Space` run `RunPrimaryAction` → `MarkActiveFileViewedAndAdvance` (marks the active file, opens
  the next unviewed file's tab, **then closes the just-reviewed file's tab**, **stops at the increment
  edge**) → then `MarkReviewedAndAdvance` once the increment is done. Closing the reviewed tab is the
  tabbed analog of collapsing a viewed file out of the way; the viewed state lives in the tracker (not the
  tab), so the file still reads checked/dimmed in the Changes list and reopens on a click. Only this
  explicit path closes tabs — the diff-header **Viewed** toggle stays a pure, reversible toggle.
- **`MarkReviewedAndAdvance` repurposed (both previously-dead methods now wired).** It was a dead
  sequential-advance; it now **marks the current increment reviewed then `NextUnreviewed()`** — so passing
  an *empty* increment marks it, and a finished tip with an earlier gap doesn't dead-end (the loop can
  always reach "complete"). `RunPrimaryAction`'s *Next increment* case calls it.
- **5.5.6 (rail polish) — minimal.** The "you are here" accent bar widened 2px → 3px; the binary
  empty/`Status.Success` dot is kept. Per-row partial progress ("2/4") stays deferred (needs per-increment
  file counts not loaded for un-opened increments, as the plan notes).
- **5.5.7 (core keyboard).** A single window-root `ReviewKeyController` (attached to the root `Box` via
  `WithController`, **never steals focus**) handles `[`/`]`, `j`/`k`, `Enter`/`Space` on **bubbling**.
  Per the framework's hover/focus dispatch (`InputSystem.SendKeyboardKeyEvent`: focused component first,
  then the hover path), it receives a key whenever the cursor is over the window and the focused file list
  hasn't consumed it — and the list only consumes its own Up/Down/Left/Right/**Enter**/Delete/Tab/F, so
  `Space`/`j`/`k`/`[`/`]` always reach the root and arrow-key list nav is untouched. **Caveat:** because
  the list consumes Enter while it holds focus, `Space` is the reliable primary key and `Enter` only
  advances when the list isn't focused — the action-bar button is always available regardless. `v`/`n`,
  the `?` cheatsheet, window-singleton/focus, and **localization** of all the new English literals stay in
  Phase 6.
- **New files:** `Features/Review/ReviewProgressMeter.cs`, `ReviewActionBar.cs`, `ReviewKeyController.cs`.
  **Edited:** `ReviewWindowViewModel` (`ReviewHud`/`ReviewPrimaryAction`, nav/file/primary methods,
  fraction + enabled readables), `ReviewHeaderBar` (meter + nav cluster + complete badge),
  `ReviewWindowRootView` (action bar + key controller), `ReviewStackList` (accent bar),
  `Features/LocalChanges/FileChangesUI.cs` + `FileChangesSection.cs` (gated viewed marks),
  `Features/Commits/CommitDetailsView.cs` (`SetReviewSha`), `CommitFileTab.cs` (`Sha`),
  `CommitDetailsTabStrip.cs` (tab Viewed mark). State stays ephemeral; no `Strings/*.json` churn.

### Phase 6 — Keyboard, localization, polish

- **Keyboard — the *remainder* (the core loop keys `j`/`k`, `[`/`]`, `Enter`/`Space` landed in
  Phase 5.5).** Add `v` mark **Viewed** and `n` **next unreviewed** (wiring `NextUnreviewed()`),
  plus `?` opens a **cheatsheet overlay** documenting the full set (ship it — every researched tool
  has one).
- **Window singleton/focus-existing:** before appending in `ReviewWindowsViewModel`, if a window for
  the same `(repoId, headRef)` is open, focus it instead (the `DiffWindows` template has no dedupe;
  we add it).
- **Localization:** add every new string (menu item, header, progress, empty/loading states,
  cheatsheet) to **all six** `Strings/*.json` — the source generator fails the build (LOC004)
  otherwise — via the `L.T(...)` / source-gen `Strings` flow.
- **Polish:** title-bar theme; stale-while-revalidate stack reload on `RefsChangedMessage` /
  `WorkingTreeChangedMessage`; skeletons; churn (`+N −M`) numbers.
- **(Optional) persistence:** JSON store under `AppPaths.AppDataPath("review-state.json")` keyed by
  `repoId|baseSha|headSha|path|contentHash`, modeled on `PreferencesStore` (atomic write, source-gen
  `JsonSerializerContext`). Caveat: SHA-keyed state doesn't survive a rebase of the range — acceptable
  (a review session is tied to specific SHAs; Phase 7's range-diff is the proper fix).

**Landed as (actual, build-verified) — and where it deviated from the steps above:**
- **Keyboard remainder — as specified, all in the one `ReviewKeyController`.** Added `v` →
  `ToggleActiveFileViewed()` (a *reversible* toggle of the active file's Viewed mark — the keyboard
  twin of the diff-header button, not the one-way action-bar advance), `n` → `NextUnreviewed()`, and
  `?` (`Slash`+`Shift`) → `ToggleCheatsheet()`. `Esc` closes the cheatsheet. The controller now reads
  the toggle from *any* state, and **while the cheatsheet is open it swallows the loop keys** (only
  `?`/`Esc` act) so they don't drive the surface behind the overlay. Arrow keys consumed by a focused
  file list before they reach the root are a deliberate non-issue (negligible behind a help overlay).
- **Cheatsheet = an in-window overlay, not a dialog.** A secondary OS window can't use the main
  window's `DialogPresenter` (it mounts into the *main* window's surface), so the cheatsheet is a
  new `ReviewCheatsheetOverlay` widget layered over the window content via the root `Stack`: a dimmed
  scrim (`0xB0000000`, dismiss-on-click + modal input block) around a centered card of `kbd`-pill
  rows. Toggled through a `Switch<bool>` over `vm.CheatsheetOpen`; the closed branch is `Empty.Widget`
  (the `SwapRegion` sentinel that hides the host outright, so it never intercepts input). **Gotcha
  found in testing:** draw order is *global by cumulative ZIndex* (`View.GetDrawZIndex` sums down the
  tree), and the diff/file panes draw their canvas content a couple levels above their box — so an
  overlay at the default ZIndex 0 had the diff text bleed through it. Fixed by pinning the scrim's
  `ZIndex = 1000` (inherited by the whole overlay subtree), matching the app's modal convention
  (`DialogSurface` = 1000, `DragOverlay` = 900, `ToastHost` = 500). A discoverable
  **`?` help button** sits on the header trailing edge (mouse twin of the key; no help glyph exists in
  the icon subset, so it's a plain "?" with a tooltip).
- **Window singleton/focus-existing — as specified.** `ReviewWindowsViewModel.OnOpenRequested` dedupes
  by `(Session.RepoId, Session.HeadRef)`; a repeat request raises a new `FocusRequested` event instead
  of appending, and `ReviewWindowsView` brings that window's OS window forward (`IWindow.Show()` +
  `Focus()`). Since Phase 1/2 set `HeadRef == HeadLabel ==` branch name, reopening the same branch
  focuses the existing review.
- **Stale-while-revalidate reload — `RefsChangedMessage` only (deliberate narrowing of the plan's
  "`RefsChanged` / `WorkingTreeChanged`").** A review stack is committed history; *every* commit-altering
  op (commit, amend, rebase, reset, merge, checkout, branch move, push/fetch — and the filesystem
  watcher for external git) broadcasts `RefsChangedMessage`, whereas `WorkingTreeChanged`-only events
  (discard, stage/unstage, an editor save via `RepoWatcher`) never change the stack and would only
  cause wasteful reloads/flicker. The reload runs on its own `GenerationGuard` lane (never invalidates
  the first load), keeps the current stack on screen until the new one arrives, preserves the selection
  when its commit survives, prunes reviewed marks to surviving commits, and re-drives the right pane
  only when the selection actually moved (a surviving sha's diff is immutable). The VM now takes
  `IMessageBus` for this.
- **Churn (`+N −M`) — filled in `GitService.LoadReviewStack`.** Each increment's churn is the
  commit-vs-first-parent line counts via LibGit2Sharp `Diff.Compare<Patch>(parentTree, commitTree)`
  (`Patch.LinesAdded`/`LinesDeleted`; file count from the patch entries; root commit diffs against the
  empty tree). The rail's existing churn slot (`ReviewStackRow`, hidden while `+0 −0`) now lights up.
  Computed on the window's background load, bounded by the 200-commit cap. The 11 `ReviewStackTests`
  still pass.
- **Localization — 23 new `review.*` keys across all six `Strings/*.json`** (window title, range
  "auto", increment position, increments/files-viewed counters, "Review complete", the three adaptive
  action labels, loading/empty-range placeholders, the three `GitReviewStackSource` error messages,
  the "Viewed" toggle, and the eight cheatsheet strings). VM count/position/label builders read
  `_loc.Strings.Value` at compute time (dialog-precedent: not live on a locale switch); the static
  widget labels use `L.T(...)` (live). The adaptive primary-action label moved into the VM as a
  `Derived<string>` (`PrimaryActionLabel`) so the action bar binds one localized readable; its icon
  stays a glyph in the view. `GitReviewStackSource` gained `ILocalizationService` (DI auto-wired).
  Window title is set once at open (the OS title bar isn't re-localized live).
- **Polish carried, not built:** title-bar theme was already done in Phase 2 (`ReviewWindowsView`).
  Loading still uses the `FadeIn` "Loading…" + the right pane's real `CommitDetailsSkeleton` (no new
  bespoke skeleton). **Persistence (optional) was not built** — viewed/reviewed state stays ephemeral
  per Open-decision #5; the content-hash "Changed since last view" auto-uncheck rides with it (moot
  until a persisted reopen). Per-row partial progress on the rail still needs un-opened increments'
  file counts and stays deferred.
- **New files:** `Features/Review/ReviewCheatsheetOverlay.cs` (overlay + `ScrimController` +
  `CardController`). **Edited:** `ReviewWindowViewModel` (`_loc`/`IMessageBus`, cheatsheet state,
  `ToggleActiveFileViewed`, `PrimaryActionLabel`, localized builders, `Reload`/`ApplyReloadedStack`),
  `ReviewKeyController` (`v`/`n`/`?`/`Esc` + open-state gating), `ReviewWindowRootView` (cheatsheet
  `Stack` layer), `ReviewHeaderBar` (help button + localized "complete"), `ReviewActionBar` (binds the
  VM label), `ReviewWindowsViewModel` (singleton + `FocusRequested`), `ReviewWindowsView` (focus
  existing), `GitReviewStackSource` (localized errors), `DiffPaneHeaderWidget` (localized "Viewed"),
  `GitService.LoadReviewStack` (churn), `Localization/Strings/*.json` (×6).

### Phase 7 — (Optional, separable) version comparator + combined diff

The Tier-2/3 differentiators; none block the MVP.

- **7.1 Range/version comparator.** Reconstruct increment snapshots from reflog/force-push history;
  a two-dropdown `{before} vs {after}` selector in the header (default left = `Base`). For "since I
  last reviewed", diff via **`git range-diff`** (not a naive two-commit diff) so a pure rebase
  doesn't read as new edits; dim/annotate rebase-only lines.
- **7.2 Combined "net diff" mode** — a header toggle **By increment** (the chain) ↔ **Combined**
  (the whole range as one diff). The only piece needing new diff plumbing: a `git diff base...head
  -- <path>` branch in `GetDiff` + a range file-list (`git diff --name-status base...head`), behind a
  new `DiffSide.Range` (or a parallel `RangeDiffTarget`). `DiffContentView` renders the result
  unchanged.
- **7.3 Range dialog** as an alternate entry + in-window "Change range…": an `IDialogViewModel` with
  base/head pickers (default base = merge-base(head, upstream/main)), following
  `MergeBranchDialogViewModel` + `ShowDialogMessage`.
- **7.4 (Later)** a history-list item ("Review from here to HEAD"); Graphite-style "upstack" gutter
  marker; conversation drawer with batched/pending comments + draft autosave.

### Phase 8 — Verification

- Build: `dotnet build GitBench\GitBench.csproj --artifacts-path <scratchpad>` (isolated outputs).
- Unit-test `LoadReviewStack` + `MergeBase` (Phase 4 list).
- Manual via `/run` + GUI MCP: open a multi-commit branch's review window; step through increments;
  confirm per-increment commit-vs-parent files + diff with highlighting + hunk actions; Viewed
  checkboxes + progress update; next-unreviewed lands; refs-change reloads; second window for a
  different branch is independent; closing one doesn't disturb the other or the main window.

## Touch list

| File | Change |
|------|--------|
| `Messages/OpenReviewWindowMessage.cs` *(new)* | open-message record (pinned session) |
| `Features/Branches/BranchesViewModel.cs` | "Review changes…" item + `StartReview` (local + remote builders) |
| `Features/Review/ReviewWindowsViewModel.cs` *(new)* | `ObservableList` of windows; subscribe; pin; singleton guard |
| `Features/Review/ReviewWindowsView.cs` *(new)* | zero-sized presenter → `ISecondaryWindowFactory.Open` |
| `App/AppView.cs` | mount `ReviewWindowsView` next to `DiffWindowsView` |
| `Features/Review/ReviewWindowRootView.cs` *(new)* | window tree: header bar + split |
| `Features/Review/ReviewWindowViewModel.cs` *(new)* | stack load via `IReviewStackSource`, selection, reviewed-state, nav; drives details; *(5.5)* `AdvanceToNextUnviewedFile`, wire `MarkReviewedAndAdvance`/`NextUnreviewed`, adaptive primary-action state, core key commands |
| `Features/Review/ReviewHeaderBar.cs` + `ReviewStackList.cs` *(new)* | range/progress bar; increment rail; *(5.5)* header progress **meter** + `‹ Prev · N of M · Next ›` cluster + adaptive primary action; rail "you are here" polish |
| `Features/Review/IReviewStackSource.cs` + `StubReviewStackSource.cs` + `GitReviewStackSource.cs` *(new)* | data seam; stub then git impl |
| `Features/Review/ReviewStack.cs` *(new)* | `ReviewSession`, `ReviewIncrement`, `ReviewStack` records |
| `Features/Commits/CommitDetailsViewModel.cs` | extract `Show(repoId, sha)` / `Clear()` from `OnCommitSelected`; *(3.5)* tab model: `OpenTabs`, `SelectFile`/`ActivateTab`/`CloseTab`/`ActiveDiff` (drops single `DiffVm`/`SelectedTarget`) |
| `Features/Commits/CommitFileTab.cs` *(new, 3.5)* | per-open-file tab: a `DiffViewModel` pinned to one `DiffTarget` |
| `Features/Commits/CommitDetailsTabStrip.cs` *(new, 3.5)* | tab strip (Details + file tabs), shared `CommitTabChrome`, `HorizontalScrollArea` reuse, dividers; *(5.5)* draw-only Viewed mark on file tabs (gated on optional `IReviewedFileTracker`) |
| `Features/Diff/DiffPaneHeaderWidget.cs` | *(3.5)* `Collapsible` flag (false in tabs — drops the collapse chevron) |
| `Git/IGitService.cs`, `Git/GitService.cs` | `MergeBase`; `LoadReviewStack` (range RevWalk, first-parent, reversed) |
| `Features/Commits/CommitDetailsView.cs` (+ `FileChangesUI.DrawFileRow`) | *(3.5)* file list + tabbed metadata/diff split, per-tab `IsVisible` body swap; *(5.5)* **draw-only** Viewed check + dim on Changes-list rows (gated on optional `IReviewedFileTracker` + current sha; no hit-test — toggle stays on the header) |
| `Localization/Strings/*.json` | new strings × all 6 locales |
| `GitBench.Tests/ReviewStackTests.cs` *(new)* | range-listing + merge-base tests |
| *(Phase 7, optional)* `Git/DiffResult.cs`, `Git/GitService.cs`, `Features/Diff/DiffViewModel.cs` | range/combined diff (`git diff base...head`, `git range-diff`) |

## Commit slices

1. **Entry point** — `OpenReviewWindowMessage` + menu item + placeholder status-bar action (Phase 1).
2. **Window shell** — `ReviewWindows{ViewModel,View}` + `ReviewWindowRootView` placeholder, mounted
   in `AppView`; the menu item opens a real empty window (Phase 2).
3. **GUI on stub** — `IReviewStackSource`+stub, `CommitDetailsViewModel.Show`, header bar + stack
   rail + reused details, selection driving diffs (Phase 3).
4. **Git backend** — `MergeBase` + `LoadReviewStack` + `GitReviewStackSource` + tests; swap the
   source (Phase 4).
5. **Reviewed-state** — increment-level, then per-file Viewed + progress + next-unreviewed (Phase 5).
5.5. **Close the review loop** — Viewed feedback on the Changes list/tabs, header progress meter +
   Prev/Next/primary-action, advance-on-view (stop at increment edge), core keys (Phase 5.5).
6. **Keyboard (remainder) + localization + singleton + polish** (Phase 6).
7. *(Optional, separable)* version comparator / combined diff / range dialog (Phase 7).

## Open decisions (recommend, but worth confirming)

1. **Right-pane reuse vs dedicated review file list.** Reuse `CommitDetailsView` (max reuse, fastest
   to running — recommended for the MVP) and add the per-file Viewed checkbox via an optional
   `IReviewedFileTracker` service; **or** give the Review window its own richer file table (status
   letter, `+/−`, comment count, Viewed) reusing only `DiffView` (matches the research's ideal
   surface but more code). Recommendation: reuse now, evolve to a dedicated list only if the Viewed
   checkbox proves awkward to graft onto the shared view.
2. **Reviewed-state granularity.** Per-file "Viewed" (recommended — the highest-value researched
   feature) with increment progress derived, vs per-increment only (simpler). Recommendation: ship
   per-increment in 5a, per-file in 5b.
3. **Default base for "Review changes…".** Merge-base with the branch's *upstream* (more correct for
   a feature-vs-its-target review) vs merge-base with the repo's *default/main* branch; fall back to
   the range dialog when neither resolves. Recommendation: upstream → default → dialog.
4. **Window dedupe.** Singleton-per-`(repo, head)` with focus-existing (recommended — a review is a
   place you return to) vs allow duplicate windows (the `DiffWindows` default).
5. **Reviewed-state persistence** in the MVP (ephemeral, recommended) vs persisted from day one
   (Phase 6 store). Persisted state is SHA-keyed and won't survive a rebase until Phase 7's
   range-diff.
6. **First-parent vs full walk** for the increment list when the range contains merges
   (recommended: first-parent for a linear, readable stack).
7. **Naming.** Menu label "Review changes…" / window title "Review: `<branch>`" (recommended — the
   feature reviews a local branch range; there is no actual PR) vs the "PR Review" framing. Code
   namespace `Features/Review/` either way.
