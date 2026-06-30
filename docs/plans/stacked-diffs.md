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

### Phase 4 — Real backend (the git range layer)

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

### Phase 5 — Reviewed-state, progress, next-unreviewed

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

### Phase 6 — Keyboard, localization, polish

- **Keyboard** (research-backed set), wired into the window's own `InputSystem`, reusing
  `ListArrowKbmController` where the file/increment lists already fit: `j`/`k` next/prev **file**;
  `]`/`[` (or `Shift+J`/`Shift+K`) next/prev **increment**; `v` mark **Viewed**; `n` **next
  unreviewed**; `Enter`/`space` mark-reviewed-and-advance; `?` opens a **cheatsheet overlay** (ship
  it — every researched tool has one).
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
| `Features/Review/ReviewWindowViewModel.cs` *(new)* | stack load via `IReviewStackSource`, selection, reviewed-state, nav; drives details |
| `Features/Review/ReviewHeaderBar.cs` + `ReviewStackList.cs` *(new)* | range/progress bar; increment rail |
| `Features/Review/IReviewStackSource.cs` + `StubReviewStackSource.cs` + `GitReviewStackSource.cs` *(new)* | data seam; stub then git impl |
| `Features/Review/ReviewStack.cs` *(new)* | `ReviewSession`, `ReviewIncrement`, `ReviewStack` records |
| `Features/Commits/CommitDetailsViewModel.cs` | extract `Show(repoId, sha)` / `Clear()` from `OnCommitSelected` |
| `Git/IGitService.cs`, `Git/GitService.cs` | `MergeBase`; `LoadReviewStack` (range RevWalk, first-parent, reversed) |
| `Features/Commits/CommitDetailsView.cs` (+ file-row) | *(Phase 5b)* optional `IReviewedFileTracker` Viewed checkbox |
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
6. **Keyboard + localization + singleton + polish** (Phase 6).
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
