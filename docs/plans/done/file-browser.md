# File browser — seeing what is on disk, without leaving the app

> **Framing:** the app already shows three views of the repository and every one of them is git's
> view — changed files, committed files, reviewed files. None of them answers "what is actually in
> this directory". That question comes up constantly next to a terminal: a build wrote something,
> a tool generated something, a dotfile needs a look. Today the answer is Finder.
>
> So this is deliberately **not** another git surface. It shows the filesystem: ignored directories,
> build output, dotfiles, empty directories — everything `ls` would show, minus `.git/`. That is the
> whole reason it earns a tab next to the three that already exist.
>
> A first draft of this plan claimed the feature was "mostly assembly." A review against the code
> knocked out four of its eight reuse claims. What survives is listed under *What already exists*;
> what didn't is called out inline, because the corrections are most of the cost.

## Decisions

| Area | Decision |
|---|---|
| Placement | A fourth mode in the switcher: **Changes │ History │ Terminal │ Files**. Full content width, tree left and preview right. |
| Per-repo | The *mode* stays app-wide (it already is). The *state* is per repo — expanded directories, cursor — and **persisted**, via the `IRepoRegistry.GetBranchesUi`/`SetBranchesUi` precedent (`IRepoRegistry.cs:59-60`), not `TerminalSessionStore`'s session-only model. A shell can't survive a restart; a set of expanded paths can, and the branches sidebar already sets that expectation. |
| What it lists | The real filesystem, enumerated lazily one directory at a time. Not `ls-files`. Ignored and hidden entries shown dimmed with a hide toggle — hiding them by default would reproduce the blind spot this exists to fix. |
| `.git/` | **Not listed.** It holds `config` (remote URLs, sometimes with tokens) and `credentials`, and it is not what "see what's on disk" means. |
| Preview | `DiffRenderState.FullFile` fed to `DiffContentView`, plus a body-kind switch for images and placeholders. The text viewer is genuinely already built; the image path is not (see A4 below). |
| Writes | **None.** No create, rename, delete, move, or drag-and-drop in v1. The feature earns its tab on visibility, which is a read; writes would drag in confirmations, undo, and a working-tree-watcher feedback loop. `IPlatformShell.OpenFolder` is the escape hatch. |
| Row type | Separate from `LocalChanges.FileRow`, which is welded to `FileChange`/`DiffSide` at every constructor (`FileTree.cs:26-64`). Generalizing it would make the changes panel worse to pay for this. |
| Live refresh | Windows/macOS only in v1 (see A1). Linux refreshes on the 30s reconcile tick and on focus gain, like the rest of the app there. |
| Out of scope for v1 | Writes, multi-select, filter/search (see F4), sorting by size or date, tabs. |

## What already exists — verified

Checked against the code, not assumed:

- **`Features/Diff/DiffContentView.cs`** is genuinely view-model-free. Its ctor needs `InputSystem`,
  `ctx.Theme()`, `ctx.Localization()` and *nullable* `IMessageBus`/`IClipboard`; all four `On*Hunk`
  callbacks are optional, and it builds its own `DiffSelectionController`. Hosting it costs a
  `Raw { View = … }`, the `ScrollBars` + `ScrollSyncController` pair and one `Bind` — the
  `DiffView.cs:42-47` recipe, about 15 lines. Not zero, but no diff machinery comes with it.
- **`DiffRenderState.FullFile`** — whole-file mode, single line-number gutter
  (`DiffRowSet.SingleGutter`), per-line syntax spans. Built for the diff pane's full-file toggle;
  a general text viewer that happens to live in the diff namespace.
- **`SyntaxHighlighter` / `ISyntaxHighlighter`** — `(text, languageId)` in, spans out, background-safe.
  Disk text to a `DiffHighlight` is three lines (`DiffHighlightCoordinator.cs:56-59`). No diff needed.
- **`ImagePreviewDecoder`** — magic-byte sniffing, PNG/JPEG, icon ladders, size cap, graceful failure.
- **`TreeMetrics`, `TreeGuides`, `RowSelection.DrawBackground`** — the indent rhythm, the trunk-mask
  guide discipline, and the selection painter. Shared as *code*; see A2 for what isn't.
- **`Widgets/ResizableSidebar.cs`** + one `Preferences` float and setter — the `BranchesSidebar.cs:15-29`
  pattern exactly.
- **The mode slot.** Commit `a84beae` added the Terminal mode in 12 files / 114 lines. The pill has no
  fixed width, no sliding indicator and no segment-count constant, so four segments lay out fine —
  but the right-hand corner radii are pinned to the literal last child (`ModeSwitcherView.cs:65`) and
  move off Terminal onto Files, the same edit `a84beae` made to History.
- **`IPlatformShell`** — `OpenFile`, `OpenFolder`, `OpenTerminal` all exist.

## Corrections to the first draft

**A1 — `RepoWatcher` does not watch the working tree on Linux.** The draft said it "already broadcasts
for ignored paths." True on Windows/macOS (`RepoWatcher.cs:207-218`, one recursive root). On Linux
`RepoWatchRoots.For` returns four narrow roots, and the only working-tree one is non-recursive and
`WatchRootKind.Gitmodules`, whose handler schedules `_submodules` and nothing else
(`RepoWatcher.cs:195-199`) — deliberately, to keep refresh latency independent of file depth, and
because a recursive inotify watch costs one watch per directory from a budget shared with the user's
editor. So on Linux the only `WorkingTreeChangedMessage` is `RepoReconcileService`'s 30-second tick
and focus gain, active repo only. v1 accepts this and documents it. The fix, if Linux matters later,
is a **browser-scoped watcher over expanded directories only** — bounded by what the user opened,
which is exactly why it doesn't hit the budget problem the narrow roots exist to avoid.

**A2 — `TreeRow` cannot be virtualized, and the changes panels don't use it.** `new TreeRow` appears
in four places: `RepoRowShell.cs:57`, `BranchListRow.cs:44`, and twice in `BranchesSkeleton.cs`. The
repo bar and the branches sidebar, exactly as its own doc comment says. The changes panels
canvas-paint through `FileChangesUI.DrawFileRow` inside a `VirtualRowListView`
(`LocalChangesPanel.cs:178,417`). `TreeRow` is a `Widget` rendered via `Each<T>`, which mints one
`View` subtree and one scoped `Context` per item with no windowing — untenable against the 40k-entry
directory this plan's own risk section names. So the browser tree is a `VirtualRowListView` with a
new `DrawFileBrowserRow` painter modelled on `FileChangesUI.DrawFileRow`: a real ~150-line module,
not a free reuse. Same split applies to navigation — `ListArrowKbmController` and `RowSelection`
belong to the virtualized-`View` world (fine), while `NavigableRowController` requires
`Features.Repos.INavigableRow` and hardcodes `RepoBarContextMenu.Show` (doesn't fit either).

**A3 — there is no batched `check-ignore`.** `IsPathIgnored(Repo, string)` is one path, one process,
one `-q` exit-code read (`GitService.cs:3698-3705`), with a single production caller on
`RepoFileGuard`'s refusal path. Expanding a 400-entry directory would be 400 spawns. Module 2 needs
a new `IsPathIgnored(Repo, IReadOnlyList<string>)` on `IGitRepositoryReader` over
`check-ignore --no-index --stdin -z`, which means dropping `-q` (exit-code-only) to parse stdout,
inverting the `result.Ok` reading (exit 1 = "none matched" is success), and updating the test fake at
`ScriptedRemoteGitService.cs:140`.

**A4 — `DiffContentView` does not render images.** `DiffRowSet.Build` flattens only `Loaded` and
`FullFile`; `Image` yields `DiffRowSet.Empty` and the placeholder switch has no case for it, so it
draws blank. Images escape one level up, in `DiffView.cs`'s `Switch<DiffBodyKind>` into
`ImagePreviewView` — which is `ctx.Require<DiffViewModel>()` at line 22. The browser needs its own
body-kind switch, and either composes the VM-free `ImagePreviewSurface` (`ImagePreviewView.cs:101`,
`SetPreview(ImagePreview?)`) directly or `ImagePreviewView` is refactored to take an
`IReadable<DiffRenderState>`.

**A5 — "Open terminal here" has no API behind it.** There is no cwd surface anywhere in the terminal
stack: `ShellLaunch`'s working directory is `readonly`, fixed at construction from `repo.Path`, and
`PtySessionOptions.WorkingDirectory` is consumed once at spawn. The options are to write `cd <path>\n`
into the running shell — which needs POSIX/PowerShell **path quoting for a pty, the first place in
this codebase that needs it** (everything else uses `ProcessStartInfo.ArgumentList`), only works once
`IsAcceptingInput`, and types into whatever program holds the foreground — or to restart the session
and lose the scrollback. v1 takes the `cd` route, and the quoting is a named deliverable.

**A6 — `FullFile` carries a `DiffSide`.** Constructing one from disk means passing a value that means
nothing here. It is inert (`_hunksPatchable` is only ever set for `Loaded`), but coding rule 1 is not
to lie to the checker. v1 passes `DiffSide.WorkingTree` with a comment; the real fix is making `Side`
nullable, which belongs with the extraction in Risks.

## Modules

**1. `IFileSystemReader` — the disk seam.**
`IReadOnlyList<FileSystemEntry> List(string absoluteDirectory)`; entry is name, is-directory,
is-symlink, is-hidden. Directories first, then ordinal-ignore-case — `PathTree.Sort` is `private`
(`PathTree.cs:69`) so the comparator is duplicated, and it needs a tiebreaker `PathTree` never did:
`README` and `readme` are one path in git and two files on a case-sensitive filesystem. Reparse points
are reported as links and never walked into; the precedent is `Infrastructure/DirectoryTree.cs`,
which learned that against `node_modules` junctions. Real implementation is
`Directory.EnumerateFileSystemEntries` off the UI thread with a per-call cancellation token; the seam
exists so flattening tests need no temp directory.

**2. `IIgnoreOracle` + `FileBrowserTree` — the cache and the flattening.**
Ignore-resolution is its own seam (the draft folded it into the tree and then called the tree "pure",
which it can't be if it shells out to git). `FileBrowserTree` holds the per-directory listing cache
and the expanded set and produces the flat `FileBrowserRow` list the pane renders and the view model
navigates — one sequence for both, the discipline `FileTreeBuilder` established. Ignored-ness is a
display attribute; it never removes a row unless the hide toggle is on.

The expanded set is `HashSet<string>` with an OS-chosen comparer (`RepoWatcher.cs:51-52` is the
precedent) so `Src/` and `src/` are one entry on macOS and Windows. Expansion is depth-capped and
refuses a link whose canonical target is an ancestor — `a -> ..` is listed safely but must not be
expandable forever; `RepoFileGuard.TryWalkInsideRepo` is the existing per-segment canonicalization.

**3. `FileBrowserStore` + `FileBrowserViewModel` — per-repo state.**
Keyed by repo id, `Active` projection swapping on `IRepoRegistry.Active`, entries dropped when a repo
leaves the registry — `TerminalSessionStore`'s mechanical shape, with persistence per Decisions.
Subscribes to `WorkingTreeChangedMessage` and **skips `IndexOnly: true`** (broadcast on hunk staging,
`DiffViewModel.cs:488` — nothing changed on disk). Note the message also fires every 30s per active
repo from `RepoReconcileService`, so invalidation must be cheap: re-list expanded directories and
diff the result, never drop the expanded set.

**4. `FileContentLoader` — bytes to a render state.**
Off-thread, cancellable: read with a size cap, NUL-sniff the first 8 KB for binary, decode text and
split lines, hand images to `ImagePreviewDecoder`. Produces `FullFile` (empty `AddedLineNumbers`, so
nothing is tinted as an addition), `Image`, or a `Placeholder`. Truncation rides the flag `FullFile`
already carries.

**5. `FileBrowserPane` — the widget.**
`ResizableSidebar` holding the virtualized tree, a body-kind switch on the right (text / image /
placeholder) per A4. Double-click expands a directory or previews a file **in-app**; opening in the
OS's default application is an explicit menu item, never the default gesture — `OpenFile` is
`UseShellExecute = true` on Windows, and unlike every existing caller the path here comes from the
filesystem, so a `.bat`/`.command`/`.lnk` dropped by a build or a hostile repo is in reach. The
codebase already defends this at the caller rather than in the shell; `TerminalLinkTarget.cs:37-42`
is the reasoning. Context menu: Open, Open in default app, Reveal in file manager, Open terminal here,
Copy path, Copy relative path — the toast strings already exist. `Path.GetFullPath` before every shell
call (a leading `-` in a filename otherwise parses as an option to `open`), and every call wrapped:
`OpenFile`/`OpenFolder`/`OpenTerminal` don't catch on macOS or Windows, and a file that vanishes
between listing and click would throw out of input dispatch.

`DiffContentView.AssistantActions` stays **false** — it defaults false and gates the only assistant
route (`DiffContentView.cs:660-661`), so this is belt-and-braces. Text selection and Ctrl+C still
work; a human pasting into the composer is a human decision, not a bypass.

**6. `IPlatformShell.RevealFile`.**
A **default interface method** falling back to `OpenFolder(Path.GetDirectoryName(path))`, so the 4
production implementors and 10 test fakes don't all need editing. macOS is `open -R`. Windows must set
`psi.Arguments` as one manually-quoted string — `ArgumentList.Add("/select,")` + `.Add(path)` yields
two arguments and explorer opens the parent instead of selecting. Linux has no reveal without a DBus
call and there is no DBus client in the repo; it takes the fallback.

**7. Wiring.** `MainViewMode.Files`, a fourth `SegmentViewModel`, a `MainContent` case, one
`app.mode.files` key across seven catalogs, one `Preferences` width field, the corner-radius move.

## Phases

Each phase names its tests, per `terminal.md`'s discipline.

1. **Seams and flattening.** `IFileSystemReader`, `IIgnoreOracle`, `FileBrowserTree`. Tests: expand and
   collapse, sort order and the case tiebreaker, symlink-cycle refusal, depth cap, a directory deleted
   while expanded, a directory renamed while expanded (the expanded set is path-keyed, so every rename
   orphans an entry unless handled). No UI.
2. **Batched ignore.** `IsPathIgnored(Repo, IReadOnlyList<string>)` over `--stdin -z`, interface, fake,
   tests for exit-1-means-none-matched and for a path containing a newline.
3. **The tab.** `MainViewMode.Files`, segment, `MainContent` case, localization, corner radii, empty pane.
   Note `MainContent.cs:20` is `KeepAlive = true`, so the pane stays mounted and subscribed once first
   visited; a tree with arrow-key navigation inherits the keyboard contract pinned by
   `GitBench.Tests/Terminal/TerminalInputSeamTests.cs:555-682`, including "walk your ancestors'
   `IsVisible`, not just your own."
4. **The tree.** `VirtualRowListView` + `DrawFileBrowserRow`, store, view model, keyboard navigation,
   ignored dimming and the hide toggle, watcher invalidation. Tests: invalidation skips `IndexOnly`,
   a re-list preserves the expanded set and the cursor.
5. **The preview.** `FileContentLoader`, body-kind switch, `DiffContentView` wiring, images, binary and
   too-large placeholders. Tests: the loader's binary / oversize / decode-failure / vanished-file branches.
6. **The escapes.** Context menu, `RevealFile`, "Open terminal here" with pty path quoting.

## Open questions

- **Empty states.** No active repo; a working tree that's missing; a bare repo — does it get a Files tab
  at all? A worktree or submodule entry. `MainContent` will show the pane in all of them and
  `MainContent.cs:28`'s default is `Empty.Widget`, not a throw, so a missed case is a silently blank
  pane cached forever under `KeepAlive`.
- **Should the mode itself persist?** It currently doesn't (`AppServices.cs:41` is a bare `State<>` with
  none of the `.Changed` hookups its neighbours have). Defensible with three tabs, more arguable with
  four. If yes, use the `Language` string-plus-lenient-parse idiom — an unknown enum string throws
  inside STJ and `PreferencesStore.Load`'s catch-all returns `Preferences.Default`, **wiping every
  other preference**.
- **A keyboard route to modes.** There is none today, and the obvious chord is taken: `Cmd/Ctrl+1..9`
  is repo hotkeys, and that's cross-wired into the terminal's hand-back logic
  (`TerminalInputController.cs:800-802`). A fourth tab makes this worse, not better.
- **RTL.** The app ships `ar.json` and RTL is threaded through the row painters. A path tree under RTL
  is a real design question and this plan is silent on it.
- **Filter text.** Cut from v1 deliberately: you can only filter what you've listed, so it either matches
  loaded rows only — surprising, since what you want is usually in a directory you haven't opened — or
  forces an eager walk that contradicts the lazy design. `PathSearch` is the seam if fuzzy find is wanted.

## Risks

- **A hostile directory.** 40k entries in one `node_modules`, or a blocking network mount. Listing is
  off-thread and per-directory and the row list is virtualized, so the failure mode is a slow expand —
  but the cap and the cancellation-on-collapse have to actually be there, which is why they're phase-1
  tests and not a sentence here.
- **Invalidation churn.** The 30s reconcile broadcast drops cache twice a minute at idle on every
  platform, and a build writing into `obj/` will fire repeatedly on macOS and Windows. Re-listing only
  expanded directories bounds it; worth measuring against a real build rather than assuming.
- **The preview is a diff type.** Reusing `DiffRenderState` couples the browser to a type owned by the
  diff feature. Acceptable while the viewer is one `DiffContentView`; the extraction, if it starts
  costing, is `FullFile` and `Image` moving to a shared file-view namespace with a nullable `Side`,
  which the diff feature then depends on.
