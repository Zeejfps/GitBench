using System.Diagnostics;
using System.IO;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitGui;

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
    public IReadable<IReadOnlyList<FileChange>> Unstaged { get; }
    public IReadable<IReadOnlyList<FileChange>> Staged { get; }
    public IReadable<IReadOnlyList<SubmoduleInfo>> DriftedSubmodules { get; }
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
    private Guid? _lastLoadedRepoId;

    // Loads run on the base Gen lane (repo switches / watcher reloads invalidate each other).
    // Mutations and commit get their own lanes so staging a file never drops an in-flight
    // reload, and a reload never drops the commit continuation (which must reset CommitBusy).
    private readonly GenerationGuard _opGen;
    private readonly GenerationGuard _commitGen;

    public LocalChangesViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        LocalChangesSelectionStore selectionStore,
        IPlatformShell shell,
        IClipboard clipboard,
        PreferencesService preferences)
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
        Unstaged = Slice(s => s.Unstaged);
        Staged = Slice(s => s.Staged);
        DriftedSubmodules = Slice(s => s.DriftedSubmodules);
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

        _commitSpinner = new SpinnerAnimation(dispatcher);
        DiffVm = new DiffViewModel(SelectedTarget, registry, gitService, dispatcher, bus);

        Update(s => s with { ViewMode = preferences.Current.FileViewMode });

        Subscriptions.Add(_registry.Active.Subscribe(_ => StartLoadForActiveRepo()));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged));
        Subscriptions.Add(_bus.SubscribeScoped<SubmodulesChangedMessage>(OnSubmodulesChanged));
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
                selection = GitGui.Selection.FromPaths(new[] { msg.Path }, moved, unstaged, staged);
            else
                selection = GitGui.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, unstaged, staged);

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

    private void OnSubmodulesChanged(SubmodulesChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active is null) return;
        var primaryId = active.IsPrimary ? active.Id : (active.ParentRepoId ?? active.Id);
        if (primaryId != msg.PrimaryRepoId) return;
        StartLoadForActiveRepo();
    }

    public override void Dispose()
    {
        DiffVm.Dispose();
        _commitSpinner.Dispose();
        base.Dispose();
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
                Selection = GitGui.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, s.Unstaged, staged),
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
                        Selection = GitGui.Selection.Create(range, anchor, clicked, s.Unstaged, s.Staged),
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
                    Selection = GitGui.Selection.Create(next, clicked, clicked, s.Unstaged, s.Staged),
                };
            }

            return s with
            {
                Selection = GitGui.Selection.Create([clicked], clicked, clicked, s.Unstaged, s.Staged),
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
            var sel = GitGui.Selection.Create(fileRows, anchor, anchor, s.Unstaged, s.Staged);
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
                        Selection = GitGui.Selection.Create(range, anchor, target, s.Unstaged, s.Staged),
                    };
                }
            }

            return s with
            {
                Selection = GitGui.Selection.Create([target], target, target, s.Unstaged, s.Staged),
            };
        });
    }

    public void ClearSelection()
    {
        if (State.Value.Selection.Count == 0 && State.Value.Selection.Anchor == null) return;
        Update(s => s with { Selection = GitGui.Selection.Empty });
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
        RunBackground<SubmoduleUpdateOutcome>(
            work: () =>
            {
                try { return (_gitService.UpdateSubmodules(repo, req), null); }
                catch (Exception ex) { return (null, ex.Message); }
            },
            onResult: (outcome, errorMsg) =>
            {
                if (errorMsg != null) { Update(s => s with { OpError = errorMsg }); return; }
                if (outcome is { Success: false }) { Update(s => s with { OpError = outcome.ErrorMessage }); return; }
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

        RunBackground<StashOutcome>(
            work: () =>
            {
                try { return (_gitService.CreateStash(repo, string.Empty, includeUntracked, keepIndex: false, paths), null); }
                catch (Exception ex) { return (null, ex.Message); }
            },
            onResult: (outcome, errorMsg) =>
            {
                if (errorMsg != null) { Update(s => s with { OpError = errorMsg }); return; }
                if (outcome is { Success: false }) { Update(s => s with { OpError = outcome.ErrorMessage }); return; }
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
        // even if a watcher-driven reload advances the load lane mid-commit — that race used
        // to leave the button permanently stuck. The post-commit snapshot is still gated on
        // the load lane (bumped here to invalidate any in-flight reload), so a reload that
        // superseded us isn't clobbered.
        var loadToken = Gen.Bump();
        RunBackground<LocalChangesSnapshot>(
            work: () =>
            {
                var err = _gitService.Commit(repo, message, amend);
                if (err != null) return (null, err);
                var got = _gitService.GetLocalChanges(repo);
                return got.ErrorMessage != null ? (null, got.ErrorMessage) : (got, null);
            },
            onResult: (snap, err) =>
            {
                _commitSpinner.Stop();
                Update(s => s with { CommitBusy = false, OpError = err });
                if (err != null) return;

                // After a successful commit the editor is cleared regardless of mode.
                // When amending we also drop the session — bypassing SetAmend(false)'s
                // restore-from-backup, which would put the pre-amend text back.
                if (_amend != null)
                {
                    _amend = null;
                    Update(s => s with { Amend = false, Title = string.Empty, Description = string.Empty });
                }
                else
                {
                    Update(s => s with { Title = string.Empty, Description = string.Empty });
                }
                if (snap != null && !Gen.IsStale(loadToken)) ApplySnapshot(snap);
                _bus.Broadcast(new CommitCreatedMessage(repo.Id));
            },
            lane: _commitGen);
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;
        StartLoadForActiveRepo();
    }

    private void StartLoadForActiveRepo()
    {
        var active = _registry.Active.Value;

        if (active == null)
        {
            // Bump to invalidate any in-flight load from the previous repo.
            Gen.Bump();
            _stagedFromIndex = Empty;
            _lastLoadedRepoId = null;
            Update(s => s with
            {
                HasRepo = false,
                IsLoading = false,
                LoadError = null,
                OpError = null,
                Staged = Empty,
                Unstaged = Empty,
                Selection = GitGui.Selection.Empty,
            });
            return;
        }

        // Cross-repo switches blank the panels (and the selection) so the "Loading…"
        // placeholder is shown rather than a stale snapshot from the previous repo.
        // Same-repo reloads (WorkingTreeChangedMessage) keep the lists visible so the
        // panels don't tear down for an incremental refresh.
        var isCrossRepoSwitch = _lastLoadedRepoId != active.Id;
        _lastLoadedRepoId = active.Id;

        Update(s => s with
        {
            HasRepo = true,
            IsLoading = true,
            LoadError = null,
            OpError = null,
            Staged = isCrossRepoSwitch ? Empty : s.Staged,
            Unstaged = isCrossRepoSwitch ? Empty : s.Unstaged,
            Selection = isCrossRepoSwitch ? GitGui.Selection.Empty : s.Selection,
        });

        var repo = active;
        // HEAD can move while amending (refs-changed reload, branch op elsewhere), so
        // refresh HEAD's file list alongside the index snapshot — otherwise the staged
        // panel keeps showing HEAD-only rows from a HEAD that no longer exists.
        var amending = _amend != null;
        // Drift state is fetched from the same place — submodules can drift independently
        // of file changes (a sibling terminal can move a submodule's HEAD without touching
        // the parent's working tree), so we always re-query alongside the snapshot.
        // Submodules themselves don't have nested submodules in our model (one level deep),
        // so skip the query when the active row is itself a submodule.
        var canQuerySubmodules = !repo.IsSubmodule;
        var loadTag = $"[LocalChanges load {repo.Path}]";
        Console.WriteLine($"{loadTag} starting (amending={amending}, querySubmodules={canQuerySubmodules})");
        var loadSw = Stopwatch.StartNew();
        RunBackground<LoadResult>(
            work: () =>
            {
                var snap = _gitService.GetLocalChanges(repo);
                if (snap.ErrorMessage != null) return (null, snap.ErrorMessage);
                var headFiles = amending ? _gitService.GetHeadCommitFiles(repo) : null;
                IReadOnlyList<SubmoduleInfo> drift = Array.Empty<SubmoduleInfo>();
                if (canQuerySubmodules)
                {
                    var subs = _gitService.ListSubmodules(repo, out _);
                    if (subs.Count > 0)
                    {
                        var driftList = new List<SubmoduleInfo>();
                        foreach (var s in subs)
                        {
                            if (s.Status == SubmoduleStatus.UpToDate) continue;
                            if (s.Status == SubmoduleStatus.Modified) continue;
                            driftList.Add(s);
                        }
                        drift = driftList;
                    }
                }
                return (new LoadResult(snap, headFiles, drift), null);
            },
            onResult: (result, errorMsg) =>
            {
                loadSw.Stop();
                Console.WriteLine($"{loadTag} finished in {loadSw.ElapsedMilliseconds}ms (error={errorMsg ?? "none"})");
                if (errorMsg != null)
                {
                    _stagedFromIndex = Empty;
                    Update(s => s with
                    {
                        IsLoading = false,
                        LoadError = errorMsg,
                        Staged = Empty,
                        Unstaged = Empty,
                        Selection = GitGui.Selection.Empty,
                        DriftedSubmodules = [],
                    });
                    return;
                }
                if (result == null) return;
                if (_amend != null && result.HeadFiles != null)
                    _amend.UpdateHeadFiles(result.HeadFiles);
                ApplySnapshot(result.Snap, result.Drift);
            });
    }

    private sealed record LoadResult(
        LocalChangesSnapshot Snap,
        IReadOnlyList<FileChange>? HeadFiles,
        IReadOnlyList<SubmoduleInfo> Drift);

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
            GitGui.Selection.Create(s.Selection.Rows, s.Selection.Anchor, s.Selection.Cursor, snap.Unstaged, staged), drift);

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
                Selection = GitGui.Selection.FromPaths(paths, toSide, unstaged, staged),
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
