using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// View model for the Local Changes feature. State lives in a single immutable
/// <see cref="LocalChangesState"/> record; <see cref="ViewModelBase{TState}.Update"/> is
/// the only mutation primitive. Views subscribe to per-field slices (auto-deduped by
/// equality) and call the command methods to drive git ops and row interactions.
///
/// Selection state lives here too — not in the panels — so the lists and the selection
/// always change in lockstep through a single <see cref="Update"/> call. That makes
/// invalid combinations (a selected path no longer in any list, the diff view targeting
/// a path that just moved sides) unrepresentable, and removes the cross-panel
/// coordination that used to be needed to keep the two sides mutually exclusive.
/// </summary>
internal sealed class LocalChangesViewModel : ViewModelBase<LocalChangesState>
{
    private static readonly IReadOnlyList<FileChange> Empty = [];

    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly LocalChangesSelectionStore _selectionStore;
    private readonly IPlatformShell _shell;
    private readonly IClipboard _clipboard;
    private readonly PreferencesService _preferences;

    public IReadable<string> Title { get; }
    public IReadable<string> Description { get; }
    public IReadable<bool> Amend { get; }
    public IReadable<string?> Placeholder { get; }
    public IReadable<string?> LoadErrorDetail { get; }
    public IReadable<IReadOnlyList<FileChange>> Unstaged { get; }
    public IReadable<IReadOnlyList<FileChange>> Staged { get; }
    public IReadable<IReadOnlyList<SubmoduleInfo>> DriftedSubmodules { get; }
    public IReadable<bool> IsMerging { get; }
    public IReadable<Selection> Selection { get; }
    public IReadable<DiffTarget?> SelectedTarget { get; }
    public IReadable<FileViewMode> ViewMode { get; }
    public IReadable<IReadOnlySet<string>> UnstagedCollapsed { get; }
    public IReadable<IReadOnlySet<string>> StagedCollapsed { get; }
    public Command Discard { get; }
    public Command StageSelected { get; }
    public Command UnstageSelected { get; }
    public Command StageAll { get; }
    public Command UnstageAll { get; }
    public Command DiscardAll { get; }
    public IReadable<string?> OpError { get; }
    public IReadable<bool> CommitEnabled { get; }
    public IReadable<bool> CommitBusy { get; }
    public IReadable<float> CommitRotation => _commitSpinner.Rotation;
    public DiffViewModel DiffVm { get; }

    private readonly SpinnerAnimation _commitSpinner;


    private IReadOnlyList<FileChange> _stagedFromIndex = Empty;
    private AmendSession? _amend;
    // True once we've populated the commit box for an in-progress merge; tracks the
    // not-merging↔merging transition so resolving conflicts doesn't re-clobber edits.
    private bool _mergeActive;
    // Repo whose working-tree data is currently reflected in state; distinguishes a cross-repo
    // switch (blank the panels) from a same-repo refresh (keep them visible).
    private Guid? _renderedRepoId;

    // Mutations and commit get their own lanes so staging a file never drops an in-flight reload,
    // and a reload never drops the commit continuation. The base Gen lane is used for the amend
    // head-files refresh. The working-tree snapshot itself is loaded by the snapshot store.
    private readonly GenerationGuard _opGen;
    private readonly GenerationGuard _commitGen;

    public LocalChangesViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus,
        LocalChangesSelectionStore selectionStore,
        IPlatformShell shell,
        IClipboard clipboard,
        PreferencesService preferences,
        IRepoSnapshotStore store)
        : base(dispatcher, LocalChangesState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _selectionStore = selectionStore;
        _shell = shell;
        _clipboard = clipboard;
        _preferences = preferences;
        _opGen = CreateLane();
        _commitGen = CreateLane();

        Title = Slice(s => s.Title);
        Description = Slice(s => s.Description);
        Amend = Slice(s => s.Amend);
        Placeholder = Slice(s => s.Placeholder);
        LoadErrorDetail = Slice(s => s.LoadErrorDetail);
        Unstaged = Slice(s => s.Unstaged);
        Staged = Slice(s => s.Staged);
        DriftedSubmodules = Slice(s => s.DriftedSubmodules);
        IsMerging = Slice(s => s.IsMerging);
        Selection = Slice(s => s.Selection);
        SelectedTarget = Slice(s => s.Selection.Single);
        ViewMode = Slice(s => s.ViewMode);
        UnstagedCollapsed = Slice(s => s.UnstagedCollapsed);
        StagedCollapsed = Slice(s => s.StagedCollapsed);
        var selectedUnstaged = Slice(s => s.Selection.Side == DiffSide.Unstaged);
        var selectedStaged = Slice(s => s.Selection.Side == DiffSide.Staged);
        var hasUnstaged = Slice(s => s.Unstaged.Count > 0);
        var hasStaged = Slice(s => s.Staged.Count > 0);
        Discard = new Command(DoDiscardSelected, selectedUnstaged);
        StageSelected = new Command(DoStageSelected, selectedUnstaged);
        UnstageSelected = new Command(DoUnstageSelected, selectedStaged);
        StageAll = new Command(DoStageAll, hasUnstaged);
        UnstageAll = new Command(DoUnstageAll, hasStaged);
        DiscardAll = new Command(DoDiscardAll, hasUnstaged);
        OpError = Slice(s => s.OpError);
        CommitEnabled = Slice(s => s.CommitEnabled);
        CommitBusy = Slice(s => s.CommitBusy);

        _commitSpinner = new SpinnerAnimation(ticker);
        DiffVm = new DiffViewModel(SelectedTarget, registry, gitService, dispatcher, bus, shell);

        Update(s => s with { ViewMode = preferences.Current.FileViewMode });

        // Working-tree file lists + submodule drift are projected from the store (which owns
        // loading, caching, and the soft refresh). The store reloads on working-tree / submodule /
        // commit changes, so this VM no longer subscribes to those messages directly.
        Subscriptions.Add(store.LocalChanges.Subscribe(OnStoreLocalChanges));
        Subscriptions.Add(_bus.SubscribeScoped<HunkAppliedOptimisticMessage>(OnHunkAppliedOptimistic));
        Subscriptions.Add(Selection.Subscribe(sel =>
            _selectionStore.UnstagedPaths.Value = sel.PathsOn(DiffSide.Unstaged)));
    }

    private void OnHunkAppliedOptimistic(HunkAppliedOptimisticMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;

        Update(s =>
        {
            var unstaged = s.Unstaged;
            var staged = s.Staged;

            FileChange? entry = msg.FromSide == DiffSide.Unstaged
                ? FindByPath(unstaged, msg.Path)
                : FindByPath(staged, msg.Path);
            if (entry == null) return s;

            if (msg.IsLastHunk)
            {
                if (msg.FromSide == DiffSide.Unstaged)
                    unstaged = RemoveByPath(unstaged, msg.Path);
                else if (msg.FromSide == DiffSide.Staged)
                    staged = RemoveByPath(staged, msg.Path);
            }

            if (msg.ToSide is DiffSide to)
            {
                if (to == DiffSide.Unstaged && FindByPath(unstaged, msg.Path) == null)
                    unstaged = InsertSorted(unstaged, entry);
                else if (to == DiffSide.Staged && FindByPath(staged, msg.Path) == null)
                    staged = InsertSorted(staged, entry);
            }

            // When the file fully moves to the other side, keep the user's focus on it by
            // shifting the selection to the destination side — same behavior as the
            // full-file stage/unstage flow in RunIndexOp.
            Selection selection;
            if (msg.IsLastHunk && msg.ToSide is DiffSide moved)
                selection = LocalChanges.Selection.FromPaths(new[] { msg.Path }, moved, unstaged, staged);
            else
                selection = LocalChanges.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, unstaged, staged);

            return s with { Unstaged = unstaged, Staged = staged, Selection = selection };
        });
    }

    private static FileChange? FindByPath(IReadOnlyList<FileChange> list, string path)
    {
        foreach (var f in list) if (f.Path == path) return f;
        return null;
    }

    private static IReadOnlyList<FileChange> RemoveByPath(IReadOnlyList<FileChange> list, string path)
    {
        var next = new List<FileChange>(list.Count);
        foreach (var f in list) if (f.Path != path) next.Add(f);
        return next;
    }

    private static IReadOnlyList<FileChange> InsertSorted(IReadOnlyList<FileChange> list, FileChange entry)
    {
        var next = new List<FileChange>(list.Count + 1);
        next.AddRange(list);
        next.Add(entry);
        next.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return next;
    }

    public override void Dispose()
    {
        DiffVm.Dispose();
        _commitSpinner.Dispose();
        base.Dispose();
    }

    // Opens the full git error block (status/submodule failure) in the scrollable
    // OperationErrorDialog. Status polls on every working-tree change, so the panel only ever
    // shows the one-line headline inline — this is the on-demand path to the whole block.
    public void ShowLoadError()
    {
        var detail = State.Value.LoadErrorDetail;
        if (string.IsNullOrEmpty(detail)) return;
        _bus.Broadcast(new ShowOperationErrorMessage("Status failed", detail));
    }

    public void SetTitle(string value)
        => Update(s => s with { Title = value });

    public void SetDescription(string value)
        => Update(s => s with { Description = value });

    public void SetAmend(bool on)
    {
        string title, description;
        if (on)
        {
            _amend = AmendSession.Begin(
                _gitService,
                _registry.Active.Value,
                State.Value.Title,
                State.Value.Description);
            title = _amend.Title;
            description = _amend.Description;
        }
        else
        {
            title = _amend?.PreAmendTitle ?? string.Empty;
            description = _amend?.PreAmendDescription ?? string.Empty;
            _amend = null;
        }

        Update(s =>
        {
            var staged = ComputeDisplayedStaged();
            return s with
            {
                Amend = on,
                Title = title,
                Description = description,
                Staged = staged,
                Selection = LocalChanges.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, s.Unstaged, staged),
            };
        });
    }
    
    /// <summary>
    /// Updates selection for a row click. Files and folders are independent selectable
    /// items: a plain click selects just the clicked row; Ctrl/Cmd toggles it; Shift
    /// extends the range from the anchor over the displayed rows (same side only). The
    /// anchor moves on plain/toggle clicks and stays put on shift-extends so subsequent
    /// shift-clicks pivot around it.
    /// </summary>
    public void SelectRow(FileRowRef clicked, InputModifiers modifiers)
    {
        var shift = (modifiers & InputModifiers.Shift) != 0;
        var toggle = (modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        var side = clicked.Side;

        Update(s =>
        {
            var rows = RowsFor(s, side);
            var clickIdx = IndexOfRow(rows, clicked);
            if (clickIdx < 0) return s;

            if (shift && s.Selection.Anchor is { } anchor && anchor.Side == side)
            {
                var anchorIdx = IndexOfRow(rows, anchor);
                if (anchorIdx >= 0)
                {
                    var lo = Math.Min(anchorIdx, clickIdx);
                    var hi = Math.Max(anchorIdx, clickIdx);
                    var range = CollectRange(rows, lo, hi);
                    return s with
                    {
                        Selection = LocalChanges.Selection.Create(range, anchor, clicked, s.Unstaged, s.Staged),
                    };
                }
            }

            if (toggle && s.Selection.Side == side)
            {
                var wasPresent = s.Selection.ContainsRow(clicked);
                var next = s.Selection.Rows.Where(r => !r.Equals(clicked)).ToList();
                if (!wasPresent) next.Add(clicked);
                return s with
                {
                    Selection = LocalChanges.Selection.Create(next, clicked, clicked, s.Unstaged, s.Staged),
                };
            }

            return s with
            {
                Selection = LocalChanges.Selection.Create([clicked], clicked, clicked, s.Unstaged, s.Staged),
            };
        });
    }

    /// <summary>Flips a folder's collapsed state (tree mode only) on the given side.</summary>
    public void ToggleFolder(DiffSide side, string folderPath)
    {
        Update(s =>
        {
            var set = side == DiffSide.Unstaged ? s.UnstagedCollapsed : s.StagedCollapsed;
            var next = new HashSet<string>(set);
            if (!next.Remove(folderPath)) next.Add(folderPath);
            return side == DiffSide.Unstaged
                ? s with { UnstagedCollapsed = next }
                : s with { StagedCollapsed = next };
        });
    }

    /// <summary>
    /// Expands every folder on the given side by clearing its collapsed set (tree mode only).
    /// </summary>
    public void ExpandAllFolders(DiffSide side)
    {
        Update(s =>
        {
            var set = side == DiffSide.Unstaged ? s.UnstagedCollapsed : s.StagedCollapsed;
            if (set.Count == 0) return s;
            var empty = new HashSet<string>();
            return side == DiffSide.Unstaged
                ? s with { UnstagedCollapsed = empty }
                : s with { StagedCollapsed = empty };
        });
    }

    /// <summary>
    /// Collapses every folder on the given side (tree mode only). The full folder set is
    /// derived from the file list with an empty collapsed set so nested folders aren't
    /// hidden from the walk.
    /// </summary>
    public void CollapseAllFolders(DiffSide side)
    {
        Update(s =>
        {
            var files = side == DiffSide.Unstaged ? s.Unstaged : s.Staged;
            var allRows = FileTreeBuilder.BuildRows(files, side, FileViewMode.Tree, EmptyCollapsed);
            var folders = new HashSet<string>();
            foreach (var row in allRows)
                if (row.Kind == FileRowKind.Folder) folders.Add(row.FullPath);
            if (folders.Count == 0) return s;
            return side == DiffSide.Unstaged
                ? s with { UnstagedCollapsed = folders }
                : s with { StagedCollapsed = folders };
        });
    }

    private static readonly IReadOnlySet<string> EmptyCollapsed = new HashSet<string>();

    /// <summary>
    /// Expands (<paramref name="expand"/> true) or collapses the folder the keyboard cursor
    /// sits on. No-op when the cursor isn't on a folder or it's already in that state.
    /// Wired to Right / Left arrows.
    /// </summary>
    public void SetCursorFolderExpanded(bool expand)
    {
        Update(s =>
        {
            if (s.Selection.Cursor is not { IsFolder: true } cur) return s;
            var side = cur.Side;
            var set = side == DiffSide.Unstaged ? s.UnstagedCollapsed : s.StagedCollapsed;
            var isCollapsed = set.Contains(cur.FullPath);
            if (expand != isCollapsed) return s;
            var next = new HashSet<string>(set);
            if (expand) next.Remove(cur.FullPath); else next.Add(cur.FullPath);
            return side == DiffSide.Unstaged
                ? s with { UnstagedCollapsed = next }
                : s with { StagedCollapsed = next };
        });
    }

    /// <summary>Switches between flat and tree view; persists the choice globally.</summary>
    public void ToggleViewMode()
    {
        var next = State.Value.ViewMode == FileViewMode.Flat ? FileViewMode.Tree : FileViewMode.Flat;
        _preferences.SetFileViewMode(next);
        Update(s =>
        {
            // Collapse the selection down to its file leaves as individual file rows: a
            // folder row carried over from tree mode has nothing to land on in flat mode,
            // and the user's intent ("these files") survives the switch either way.
            var fileRows = s.Selection.Items
                .Select(t => new FileRowRef(t.Side, t.Path, IsFolder: false))
                .ToList();
            FileRowRef? anchor = fileRows.Count > 0 ? fileRows[0] : null;
            var sel = LocalChanges.Selection.Create(fileRows, anchor, anchor, s.Unstaged, s.Staged);
            return s with { ViewMode = next, Selection = sel };
        });
    }

    private static IReadOnlyList<FileRow> RowsFor(LocalChangesState s, DiffSide side)
    {
        var files = side == DiffSide.Unstaged ? s.Unstaged : s.Staged;
        var collapsed = side == DiffSide.Unstaged ? s.UnstagedCollapsed : s.StagedCollapsed;
        return FileTreeBuilder.BuildRows(files, side, s.ViewMode, collapsed);
    }

    private static int IndexOfRow(IReadOnlyList<FileRow> rows, FileRowRef r)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.FullPath == r.FullPath && (row.Kind == FileRowKind.Folder) == r.IsFolder)
                return i;
        }
        return -1;
    }

    // The row refs spanned by rows[lo..hi] — every file and folder row in the range is
    // selected as its own item; folder expansion to files happens in Selection.Create.
    private static List<FileRowRef> CollectRange(IReadOnlyList<FileRow> rows, int lo, int hi)
    {
        var refs = new List<FileRowRef>(hi - lo + 1);
        for (var i = lo; i <= hi; i++) refs.Add(rows[i].Ref);
        return refs;
    }

    /// <summary>
    /// Moves the keyboard cursor up or down within the currently active side, clamping
    /// at the list edges. With no selection, the first press lands on the top or bottom
    /// row of the unstaged side (or the staged side if unstaged is empty). When
    /// <paramref name="extend"/> is true the anchor stays put and the range grows or
    /// shrinks toward the new cursor position; otherwise the selection collapses to
    /// the single new row.
    /// </summary>
    public void MoveSelection(int delta, bool extend)
    {
        if (delta == 0) return;

        Update(s =>
        {
            DiffSide side;
            if (s.Selection.Side is { } selSide) side = selSide;
            else if (s.Unstaged.Count > 0) side = DiffSide.Unstaged;
            else if (s.Staged.Count > 0) side = DiffSide.Staged;
            else return s;

            var rows = RowsFor(s, side);
            if (rows.Count == 0) return s;

            int currentIdx = s.Selection.Cursor is { } cur && cur.Side == side
                ? IndexOfRow(rows, cur)
                : -1;
            if (currentIdx < 0)
                currentIdx = delta > 0 ? -1 : rows.Count;

            var newIdx = Math.Clamp(currentIdx + delta, 0, rows.Count - 1);
            if (newIdx == currentIdx && s.Selection.Count > 0 && !extend) return s;

            var target = rows[newIdx].Ref;

            if (extend && s.Selection.Anchor is { } anchor && anchor.Side == side)
            {
                var anchorIdx = IndexOfRow(rows, anchor);
                if (anchorIdx >= 0)
                {
                    var lo = Math.Min(anchorIdx, newIdx);
                    var hi = Math.Max(anchorIdx, newIdx);
                    var range = CollectRange(rows, lo, hi);
                    return s with
                    {
                        Selection = LocalChanges.Selection.Create(range, anchor, target, s.Unstaged, s.Staged),
                    };
                }
            }

            return s with
            {
                Selection = LocalChanges.Selection.Create([target], target, target, s.Unstaged, s.Staged),
            };
        });
    }

    public void ClearSelection()
    {
        if (State.Value.Selection.Count == 0 && State.Value.Selection.Anchor == null) return;
        Update(s => s with { Selection = LocalChanges.Selection.Empty });
    }
    
    private void DoDiscardSelected()
    {
        var paths = State.Value.Selection.PathsOn(DiffSide.Unstaged);
        if (paths.Count == 0) return;
        RequestDiscard(paths);
    }

    private void DoStageSelected()
        => Stage(State.Value.Selection.PathsOn(DiffSide.Unstaged));

    private void DoUnstageSelected()
        => Unstage(State.Value.Selection.PathsOn(DiffSide.Staged));

    private void DoStageAll()
        => Stage(State.Value.Unstaged.Select(f => f.Path).ToList());

    private void DoUnstageAll()
        => Unstage(State.Value.Staged.Select(f => f.Path).ToList());

    private void DoDiscardAll()
        => RequestDiscard(State.Value.Unstaged.Select(f => f.Path).ToList());

    public void Stage(IReadOnlyList<string> paths) => RunIndexOp(paths, isStage: true);

    public void StageSubmodulePointer(string submodulePath) => RunIndexOp([submodulePath], isStage: true);

    // The subset of the given paths that are still conflicted (unmerged) in the unstaged
    // panel — drives the "Mark as Resolved" context-menu item, which only applies to those.
    public IReadOnlyList<string> ConflictedAmong(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var set = new HashSet<string>(paths);
        var result = new List<string>();
        foreach (var f in State.Value.Unstaged)
            if (f.Status == FileChangeStatus.Conflicted && set.Contains(f.Path))
                result.Add(f.Path);
        return result;
    }

    // Marks externally-resolved conflicts as resolved: stages the working-tree file as-is
    // (git add), or records the deletion (git rm) when the resolution removed the file, so the
    // path clears the unmerged state and the user can continue the merge/rebase. Mirrors the
    // stage flow — optimistic move to the staged side, then the index mutation with a
    // working-tree refresh that reconciles the real post-resolve status.
    public void MarkResolved(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var repo = _registry.Active.Value;
        if (repo == null) return;

        ApplyOptimisticMove(paths, DiffSide.Unstaged);

        RunBackground<bool>(
            work: () =>
            {
                string? firstError = null;
                foreach (var path in paths)
                {
                    if (_gitService.MarkResolved(repo, path) is GitOutcome.Failed failed)
                        firstError ??= failed.Message;
                }
                return (true, firstError);
            },
            onResult: (_, errorMsg) =>
            {
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                Update(s => s with { OpError = errorMsg });
            },
            lane: _opGen);
    }

    // Resets a submodule's working tree back to the SHA the parent has recorded. Runs
    // `git submodule update -- <path>` and broadcasts SubmodulesChangedMessage so the
    // drift list refreshes once the watcher / re-load catches up.
    public void ResetSubmoduleToRecorded(string submodulePath)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var req = new SubmoduleUpdateRequest(
            Paths: [submodulePath],
            Init: false,
            Recursive: false,
            Mode: SubmoduleUpdateMode.Checkout);
        var primaryId = repo.IsPrimary ? repo.Id : (repo.ParentRepoId ?? repo.Id);
        RunBackground<MergeLikeOutcome>(
            work: () => (_gitService.UpdateSubmodules(repo, req), null),
            onResult: (outcome, errorMsg) =>
            {
                if (MergeLikeOutcome.Normalize(outcome, errorMsg) is MergeLikeOutcome.Failed failed)
                {
                    Update(s => s with { OpError = failed.Message });
                    return;
                }
                _bus.Broadcast(new SubmodulesChangedMessage(primaryId));
            },
            lane: _opGen);
    }

    public void Unstage(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        // While amending, the staged panel may include HEAD-only files (not in the
        // index) that the user wants to drop from the amended commit. Those need a
        // reset against HEAD~1; truly-staged files take the normal unstage path.
        if (_amend != null && _amend.HeadFiles.Count > 0)
        {
            var (toUnstage, toResetToParent) = _amend.Classify(paths, _stagedFromIndex);
            if (toResetToParent.Count > 0)
            {
                RunUnstageWithReset(toUnstage, toResetToParent);
                return;
            }
        }
        RunIndexOp(paths, isStage: false);
    }

    // Routes through the bus so DialogPresenter owns the modal lifecycle; the dialog's
    // own presenter runs the git op and broadcasts RefsChangedMessage on success, which
    // brings us back through OnRefsChanged to reload the snapshot.
    public void RequestDiscard(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new DiscardChangesDialog(repo, paths, onClose)));
    }

    // Stashes the working-tree changes for the given paths (git's default "WIP on…"
    // message). Pulls in untracked entries when any selected path is untracked, since
    // `git stash push -- <path>` skips those otherwise.
    public void StashSelected(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var repo = _registry.Active.Value;
        if (repo == null) return;

        var untracked = new HashSet<string>();
        foreach (var f in State.Value.Unstaged)
            if (f.Status == FileChangeStatus.Added) untracked.Add(f.Path);
        var includeUntracked = paths.Any(untracked.Contains);

        RunBackground<GitOutcome>(
            work: () => (_gitService.CreateStash(repo, string.Empty, includeUntracked, keepIndex: false, paths), null),
            onResult: (outcome, errorMsg) =>
            {
                if (GitOutcome.Normalize(outcome, errorMsg) is GitOutcome.Failed failed)
                {
                    Update(s => s with { OpError = failed.Message });
                    return;
                }
                _bus.Broadcast(new RefsChangedMessage(repo.Id));
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
            },
            lane: _opGen);
    }

    public void CopyPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    public void CopyAbsolutePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => Path.Combine(repo.Path, p))));
    }

    public void CopyFileNames(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _clipboard.SetText(string.Join(Environment.NewLine, paths.Select(Path.GetFileName)));
    }

    public void OpenContainingFolder(string path)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var dir = Path.GetDirectoryName(Path.Combine(repo.Path, path));
        if (!string.IsNullOrEmpty(dir)) _shell.OpenFolder(dir);
    }

    public void OpenInTerminal(string path)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var dir = Path.GetDirectoryName(Path.Combine(repo.Path, path));
        if (!string.IsNullOrEmpty(dir)) _shell.OpenTerminal(dir);
    }

    public void Commit()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        if (State.Value.CommitBusy) return;

        // CommitEnabled gates the button on a non-empty title (and, unless amending, at
        // least one staged file), so reaching this point implies the inputs are valid.
        var snapshot = State.Value;
        var title = snapshot.Title.Trim();
        var description = snapshot.Description.Trim();
        // Standard git format: subject, blank line, body. Skip the blank line when there's
        // no body so the message is just the subject.
        var message = description.Length > 0 ? $"{title}\n\n{description}" : title;
        var amend = snapshot.Amend;

        Update(s => s with { CommitBusy = true, OpError = null });
        _commitSpinner.Start();

        // Commit runs on its own lane so the continuation always runs (resetting CommitBusy)
        // even if a store reload lands mid-commit. The editor is cleared immediately; the
        // post-commit file lists arrive via CommitCreatedMessage → the store reloads and pushes
        // the fresh snapshot through OnStoreLocalChanges.
        RunBackground<bool>(
            work: () =>
            {
                var err = _gitService.Commit(repo, message, amend);
                return err == null ? (true, null) : (false, err);
            },
            onResult: (_, err) =>
            {
                _commitSpinner.Stop();
                if (err != null)
                {
                    Update(s => s with { CommitBusy = false, OpError = err });
                    return;
                }

                // A successful commit folds every staged file into the new commit, so clear
                // the staged side optimistically rather than waiting for the store's post-commit
                // reload (CommitCreatedMessage → ReloadLocal) to round-trip — that lag is why the
                // panel used to linger for ~a second. The reload reconciles to the same empty
                // staged list, so there's no flicker.
                _stagedFromIndex = Empty;

                // After a successful commit the editor is cleared regardless of mode.
                // When amending we also drop the session — bypassing SetAmend(false)'s
                // restore-from-backup, which would put the pre-amend text back.
                _amend = null;
                Update(s => s with
                {
                    CommitBusy = false,
                    OpError = null,
                    Amend = false,
                    Title = string.Empty,
                    Description = string.Empty,
                    Staged = Empty,
                    Selection = LocalChanges.Selection.Create(
                        s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, s.Unstaged, Empty),
                });
                _bus.Broadcast(new CommitCreatedMessage(repo.Id));
            },
            lane: _commitGen);
    }

    // Projection of the store's local-changes slice (file lists + submodule drift). data == null
    // means no data yet for the active repo (switching / cache miss) → blank or loading; a cross-
    // repo switch blanks the panels, a same-repo refresh keeps them visible. While amending we
    // refresh HEAD's file list off-thread first (HEAD may have moved), then apply.
    private void OnStoreLocalChanges(LocalChangesData? data)
    {
        var active = _registry.Active.Value;
        if (active == null)
        {
            _stagedFromIndex = Empty;
            _renderedRepoId = null;
            _mergeActive = false;
            Update(s => s with
            {
                HasRepo = false,
                IsLoading = false,
                LoadError = null,
                LoadErrorDetail = null,
                OpError = null,
                Staged = Empty,
                Unstaged = Empty,
                Selection = LocalChanges.Selection.Empty,
                DriftedSubmodules = [],
                IsMerging = false,
            });
            return;
        }

        var isCrossRepoSwitch = _renderedRepoId != active.Id;
        _renderedRepoId = active.Id;

        if (data == null)
        {
            // No data for the active repo yet. Cross-repo switch blanks the panels; a same-repo
            // gap just shows the loading flag while keeping the current lists.
            Update(s => s with
            {
                HasRepo = true,
                IsLoading = true,
                LoadError = null,
                LoadErrorDetail = null,
                OpError = null,
                Staged = isCrossRepoSwitch ? Empty : s.Staged,
                Unstaged = isCrossRepoSwitch ? Empty : s.Unstaged,
                Selection = isCrossRepoSwitch ? LocalChanges.Selection.Empty : s.Selection,
            });
            return;
        }

        var snap = data.Snapshot;
        if (snap.ErrorMessage != null)
        {
            _stagedFromIndex = Empty;
            Update(s => s with
            {
                HasRepo = true,
                IsLoading = false,
                LoadError = snap.ErrorMessage,
                LoadErrorDetail = snap.ErrorDetail ?? snap.ErrorMessage,
                Staged = Empty,
                Unstaged = Empty,
                Selection = LocalChanges.Selection.Empty,
                DriftedSubmodules = [],
            });
            return;
        }

        // On a cross-repo switch the selection belongs to the previous repo — drop it before
        // applying (ApplySnapshot's reload-style path would otherwise try to carry it forward).
        if (isCrossRepoSwitch && State.Value.Selection.Count > 0)
            Update(s => s with { Selection = LocalChanges.Selection.Empty });

        Update(s => s.HasRepo ? s : s with { HasRepo = true });

        HandleMergeState(data.MergeMessage);

        if (_amend != null)
        {
            // HEAD may have moved (e.g. an external commit) while amending — refresh HEAD's file
            // list off-thread before applying so the staged panel's HEAD-only rows stay valid.
            // Uses the base Gen lane: a newer push supersedes an older in-flight refresh.
            var repo = active;
            var drift = data.Drift;
            RunBackground<IReadOnlyList<FileChange>>(
                work: () => (_gitService.GetHeadCommitFiles(repo), null),
                onResult: (headFiles, _) =>
                {
                    if (_amend != null && headFiles != null && _registry.Active.Value?.Id == repo.Id)
                        _amend.UpdateHeadFiles(headFiles);
                    ApplySnapshot(snap, drift);
                });
            return;
        }

        ApplySnapshot(snap, data.Drift);
    }

    // Drives the merge-aware commit box from the store's merge message. On entering a merge,
    // pre-fills the box with the default merge message (so committing finishes the merge); on
    // leaving (committed or aborted), clears it. Only the entry/exit transitions touch the
    // editor — staging a conflict mid-merge reloads but must not overwrite the user's edits.
    private void HandleMergeState(string? mergeMessage)
    {
        var isMerging = mergeMessage != null;
        if (isMerging && !_mergeActive)
        {
            _mergeActive = true;
            if (_amend == null)
            {
                var (title, description) = SplitMergeMessage(mergeMessage!);
                Update(s => s with { IsMerging = true, Title = title, Description = description });
            }
            else
            {
                Update(s => s with { IsMerging = true });
            }
        }
        else if (!isMerging && _mergeActive)
        {
            _mergeActive = false;
            Update(s => s with { IsMerging = false, Title = string.Empty, Description = string.Empty });
        }
        else if (State.Value.IsMerging != isMerging)
        {
            Update(s => s with { IsMerging = isMerging });
        }
    }

    // Splits the raw MERGE_MSG into editor title/description: drop git's '#'-prefixed comment
    // lines (the "Conflicts:" hint), then take the first line as the subject and the rest as body.
    private static (string Title, string Description) SplitMergeMessage(string message)
    {
        var kept = new List<string>();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            if (!line.StartsWith("#", StringComparison.Ordinal)) kept.Add(line);
        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        if (kept.Count == 0) return (string.Empty, string.Empty);
        var title = kept[0].Trim();
        var body = kept.Count > 1 ? string.Join("\n", kept.Skip(1)).Trim() : string.Empty;
        return (title, body);
    }

    // Writes a fresh snapshot — new lists plus whatever selection the caller computes
    // from them — through a single atomic Update. The selection callback receives the
    // pre-update state (so reload-style callers can carry the prior selection through
    // Selection.Create normalization) and the newly-computed displayed staged (so all
    // callers see the same amend-aware view of the staged side the state is about to
    // hold). When drift is null, the existing DriftedSubmodules stays put — used by
    // stage/unstage/commit, which only mutate the file lists.
    private void ApplySnapshot(
        LocalChangesSnapshot snap,
        Func<LocalChangesState, IReadOnlyList<FileChange>, Selection> selectionFor,
        IReadOnlyList<SubmoduleInfo>? drift = null)
    {
        _stagedFromIndex = snap.Staged;
        Update(s =>
        {
            var staged = ComputeDisplayedStaged();
            return s with
            {
                IsLoading = false,
                LoadError = null,
                LoadErrorDetail = null,
                Unstaged = snap.Unstaged,
                Staged = staged,
                Selection = selectionFor(s, staged),
                DriftedSubmodules = drift ?? s.DriftedSubmodules,
            };
        });
    }

    // Reload-style apply: keep the existing selection (paths still in the lists survive,
    // gone paths are pruned by Selection.Create). Used for cross-repo reloads, watcher
    // ticks, refs changes, and post-commit snapshots — anywhere the lists change but the
    // selection isn't being explicitly steered to a new place.
    private void ApplySnapshot(LocalChangesSnapshot snap, IReadOnlyList<SubmoduleInfo>? drift = null)
        => ApplySnapshot(snap, (s, staged) =>
            LocalChanges.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, snap.Unstaged, staged), drift);

    private void RunIndexOp(IReadOnlyList<string> paths, bool isStage)
    {
        if (paths.Count == 0) return;
        var repo = _registry.Active.Value;
        if (repo == null) return;

        ApplyOptimisticMove(paths, isStage ? DiffSide.Unstaged : DiffSide.Staged);

        RunIndexMutation(repo, () =>
        {
            if (isStage) _gitService.Stage(repo, paths);
            else _gitService.Unstage(repo, paths);
        });
    }

    private void RunIndexMutation(Repo repo, Action mutate)
    {
        RunBackground<bool>(
            work: () => { mutate(); return (true, null); },
            onResult: (_, errorMsg) =>
            {
                _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                Update(s => s with { OpError = errorMsg });
            },
            lane: _opGen);
    }

    private void ApplyOptimisticMove(IReadOnlyList<string> paths, DiffSide fromSide)
    {
        DiffVm.DeferReloadToWorkingTreeChange();
        var toSide = fromSide == DiffSide.Unstaged ? DiffSide.Staged : DiffSide.Unstaged;
        Update(s =>
        {
            var unstaged = s.Unstaged;
            var staged = s.Staged;
            foreach (var path in paths)
            {
                var entry = fromSide == DiffSide.Unstaged
                    ? FindByPath(unstaged, path)
                    : FindByPath(staged, path);
                if (entry == null) continue;

                if (fromSide == DiffSide.Unstaged)
                {
                    unstaged = RemoveByPath(unstaged, path);
                    if (FindByPath(staged, path) == null) staged = InsertSorted(staged, entry);
                }
                else
                {
                    staged = RemoveByPath(staged, path);
                    if (FindByPath(unstaged, path) == null) unstaged = InsertSorted(unstaged, entry);
                }
            }
            return s with
            {
                Unstaged = unstaged,
                Staged = staged,
                Selection = LocalChanges.Selection.FromPaths(paths, toSide, unstaged, staged),
            };
        });
    }

    private void RunUnstageWithReset(IReadOnlyList<string> toUnstage, IReadOnlyList<string> toResetToParent)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;

        // Both batches land on the unstaged side after the reset/unstage.
        var movedToUnstaged = new List<string>(toUnstage.Count + toResetToParent.Count);
        movedToUnstaged.AddRange(toUnstage);
        movedToUnstaged.AddRange(toResetToParent);

        ApplyOptimisticMove(movedToUnstaged, DiffSide.Staged);

        RunIndexMutation(repo, () =>
        {
            if (toUnstage.Count > 0) _gitService.Unstage(repo, toUnstage);
            if (toResetToParent.Count > 0) _gitService.ResetToParent(repo, toResetToParent);
        });
    }

    private IReadOnlyList<FileChange> ComputeDisplayedStaged()
        => _amend?.MergeWithIndex(_stagedFromIndex) ?? _stagedFromIndex;
}
