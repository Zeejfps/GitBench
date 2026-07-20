using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Controls;
using GitBench.Localization;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The Changes tab's Review layout: the working tree's changed files stacked in one scrolling diff
/// surface, the same one a branch review uses. A file's header checkbox is its staged state — checking
/// it stages the file.
///
/// Files come from <see cref="LocalChangesViewModel"/> (which already owns the working-tree snapshot
/// and the index ops), not from git directly. Each file diffs HEAD→disk, so staging it never changes
/// what its card shows.
/// </summary>
internal sealed class WorkingTreeReviewViewModel : IReviewSurfaceModel, IDisposable
{
    private readonly LocalChangesViewModel _local;
    private readonly CommitDetailsViewModel _details;
    private readonly IRepoRegistry _registry;
    private readonly ILocalizationService _loc;
    private readonly StagedFileTracker _marks;
    private readonly ReviewFileCursor _cursor;
    private readonly FileOpsContextMenu _fileOps;

    private readonly State<bool> _cheatsheetOpen = new(false);
    private readonly Derived<ReviewHud> _hud;
    private readonly Derived<float> _filesFraction;
    private readonly Derived<string> _filesStagedLabel;
    private readonly Derived<bool> _hasFiles;
    private readonly Derived<bool> _canStageSelected;
    private readonly Derived<bool> _canUnstageSelected;
    private readonly Derived<bool> _canDiscardSelected;
    private readonly List<IDisposable> _subscriptions = new();

    public WorkingTreeReviewViewModel(
        LocalChangesViewModel local,
        CommitDetailsViewModel details,
        IRepoRegistry registry,
        ILocalizationService loc)
    {
        _local = local;
        _details = details;
        _registry = registry;
        _loc = loc;
        _marks = new StagedFileTracker(local);
        _cursor = new ReviewFileCursor(Files, _marks);
        _fileOps = new FileOpsContextMenu(local, loc);

        _hud = new Derived<ReviewHud>(BuildHud);
        _filesFraction = new Derived<float>(() => _hud.Value.FilesFraction);
        _filesStagedLabel = new Derived<string>(BuildFilesStagedLabel);
        _hasFiles = new Derived<bool>(() => Files().Count > 0);
        _canStageSelected = new Derived<bool>(() => AnySelected(p => !_marks.IsViewed(p)));
        _canUnstageSelected = new Derived<bool>(() => AnySelected(_marks.HasStagedContent));
        _canDiscardSelected = new Derived<bool>(() => AnySelected(p => !_marks.IsViewed(p)));
        StageSelected = new Command(() => SetSelectedStaged(true), _canStageSelected);
        UnstageSelected = new Command(() => SetSelectedStaged(false), _canUnstageSelected);
        DiscardSelected = new Command(DoDiscardSelected, _canDiscardSelected);

        // The working tree changes on every editor save and every index op. Re-push the merged file
        // list each time; the details surface keeps its open diffs and the stacked list reconciles in
        // place, so the reviewer's scroll and loaded diffs survive. Subscribed to the atomic pair —
        // reading the two side slices from a handler of either can catch the other mid-update, and the
        // resulting file-less transient knocks the cursor off the active file.
        _subscriptions.Add(_local.WorkingTreeLists.Subscribe(PushFiles));

        // A folder and a file are peers in the tree's cursor, so landing on a folder drops the file
        // selection — one row is current at a time, as in the list layout.
        _subscriptions.Add(_details.CursorFolder.Subscribe(folder =>
        {
            if (folder != null) _cursor.ClearSelection();
        }));
    }

    public ReviewMarkKind MarkKind => ReviewMarkKind.Staged;
    public IReviewedFileTracker ReviewedFiles => _marks;
    public CommitDetailsViewModel Details => _details;

    public IReadable<string?> ActiveFile => _cursor.ActiveFile;
    public IReadable<IReadOnlySet<string>> SelectedPaths => _cursor.SelectedPaths;
    public IReadable<string?> SelectionCursor => _cursor.SelectionCursor;
    public IReadable<ReviewHud> Hud => _hud;
    public IReadable<bool> CheatsheetOpen => _cheatsheetOpen;

    /// <summary>Stages every selected file that isn't fully staged yet; a partially staged one has more
    /// to capture, so it still counts.</summary>
    public Command StageSelected { get; }

    /// <summary>Unstages every selected file carrying any staged content, partial ones included.</summary>
    public Command UnstageSelected { get; }

    /// <summary>Opens the discard confirmation for every selected file with unstaged edits.</summary>
    public Command DiscardSelected { get; }

    // The review surface's file list is the whole working tree, so the list layout's all-files commands
    // already cover exactly the right paths — gate and action both.
    public Command StageAll => _local.StageAll;
    public Command UnstageAll => _local.UnstageAll;

    /// <summary>0..1 progress of the header meter: files staged across the working tree.</summary>
    public IReadable<float> FilesFraction => _filesFraction;

    /// <summary>"N / M files staged", empty when there is nothing to review.</summary>
    public IReadable<string> FilesStagedLabel => _filesStagedLabel;

    /// <summary>False when the working tree is clean — the surface shows its empty state instead.</summary>
    public IReadable<bool> HasFiles => _hasFiles;

    public event Action<string>? ScrollToFileRequested
    {
        add => _cursor.ScrollToFileRequested += value;
        remove => _cursor.ScrollToFileRequested -= value;
    }

    public bool IsFileViewed(string path) => _marks.IsViewed(path);
    public bool IsFilePartiallyMarked(string path) => _marks.IsPartiallyStaged(path);
    public void ToggleFileViewed(string path) => _marks.ToggleViewed(path);

    /// <summary>
    /// Enter / Space / v on the tree: stages the target, or unstages it when it is already fully
    /// staged. With a folder under the cursor the target is its whole subtree, so one keypress stages
    /// a folder the way it stages a file.
    /// </summary>
    public void ToggleActiveFileViewed()
    {
        if (_details.CursorFolder.Value == null) _cursor.ToggleActiveFileMarked();
        else _cursor.ToggleMarked(TargetPaths());
    }

    public void ReportActiveFile(string path)
    {
        // A click into the diff picks a file, so the tree's folder cursor yields — the panel does this
        // for its own row clicks, but a diff-card click reaches the model without passing through it.
        _details.SetCursorFolder(null);
        _cursor.ReportActiveFile(path);
    }
    public void ActivateFile(string path) => _cursor.ActivateFile(path);

    public void SelectFile(string path, InputModifiers modifiers, IReadOnlyList<string> visiblePaths)
        => _cursor.SelectFile(path, modifiers, visiblePaths);

    public void SelectAllFiles(IReadOnlyList<string> visiblePaths) => _cursor.SelectAllFiles(visiblePaths);

    public void NextFile() => _cursor.NextFile();
    public void PrevFile() => _cursor.PrevFile();

    public void ToggleCheatsheet() => _cheatsheetOpen.Value = !_cheatsheetOpen.Value;
    public void CloseCheatsheet() => _cheatsheetOpen.Value = false;

    /// <summary>A file's right-click menu: the same file operations the list layout offers, over the
    /// whole selection when the file is part of it.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildFileContextMenuItems(string path)
        => BuildFileOpsMenuItems(_cursor.ResolveTargetPaths(path), path);

    /// <summary>A folder row's right-click menu: the same operations over every file beneath it, with
    /// the folder itself as the open-folder / terminal target, plus folding of its own subtree.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildTreeFolderContextMenuItems(string folderPath, IReadOnlyList<string> paths)
    {
        var items = BuildFileOpsMenuItems(paths, folderPath);
        FileTreeFoldingMenu.AppendForFolder(items, _details, _loc, folderPath);
        return items;
    }

    /// <summary>Right-clicking below the last row: the whole-tree commands only — stage/unstage
    /// everything, and folding across the entire tree.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildTreeEmptyContextMenuItems()
    {
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>
        {
            new(s.LocalchangesStageAllMenu, StageAll.Execute, LucideIcons.ChevronsRight,
                Enabled: StageAll.CanExecute.Value),
            new(s.LocalchangesUnstageAllMenu, UnstageAll.Execute, LucideIcons.ChevronsLeft,
                Enabled: UnstageAll.CanExecute.Value),
        };
        FileTreeFoldingMenu.AppendForTree(items, _details, _loc);
        return items;
    }

    private List<RepoBarContextMenu.Item> BuildFileOpsMenuItems(IReadOnlyList<string> targets, string representative)
    {
        var items = new List<RepoBarContextMenu.Item>();
        _fileOps.AppendFileOps(items, targets);
        if (targets.Count > 0) _fileOps.AppendUtilities(items, targets, representative);
        return items;
    }

    // Re-projects the working tree into the details surface as one file list. A path present on both
    // sides (staged, then edited again) takes its staged entry: that status is the one measured
    // against HEAD, which is what the file's diff shows.
    private void PushFiles((IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged) lists)
    {
        if (_registry.Active.Value is not { } repo)
        {
            _details.Clear();
            return;
        }

        var merged = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        foreach (var f in lists.Unstaged) merged[f.Path] = f;
        foreach (var f in lists.Staged) merged[f.Path] = f;

        var files = new List<FileChange>(merged.Values);
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        _details.ShowWorkingTree(repo.Id, files);
        _cursor.OnFilesLoaded(files);
    }

    /// <summary>
    /// What Stage / Unstage / Discard act on: every file under the folder when the tree's cursor sits
    /// on a folder row, else the selected files. A folder is a stand-in for its contents, so staging
    /// with one selected stages the subtree.
    /// </summary>
    private IReadOnlyList<string> TargetPaths()
        => _details.CursorFolder.Value is { } folder
            ? _cursor.PathsUnder(folder)
            : [.. _cursor.SelectedPaths.Value];

    // Reads the tracker's revision as well as the targets so the gate re-evaluates after an index op.
    private bool AnySelected(Func<string, bool> predicate)
    {
        _ = _marks.Revision.Value;
        foreach (var p in TargetPaths())
            if (predicate(p)) return true;
        return false;
    }

    private void SetSelectedStaged(bool staged) => _marks.SetViewed(TargetPaths(), staged);

    private void DoDiscardSelected()
    {
        var paths = new List<string>();
        foreach (var p in TargetPaths())
            if (!_marks.IsViewed(p)) paths.Add(p);
        _local.RequestDiscard(paths);
    }

    private ReviewHud BuildHud()
    {
        var files = Files();
        var staged = _cursor.CountMarked(files);
        return new ReviewHud(
            FilesViewed: staged,
            FilesTotal: files.Count,
            IsComplete: files.Count > 0 && staged >= files.Count);
    }

    private string BuildFilesStagedLabel()
    {
        var hud = _hud.Value;
        return hud.FilesTotal == 0
            ? string.Empty
            : _loc.Strings.Value.ReviewFilesStaged(hud.FilesViewed, hud.FilesTotal);
    }

    private IReadOnlyList<FileChange> Files() =>
        _details.RenderState.Value is CommitDetailsRenderState.Loaded l
            ? l.Details.Files
            : Array.Empty<FileChange>();

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _canDiscardSelected.Dispose();
        _canUnstageSelected.Dispose();
        _canStageSelected.Dispose();
        _hasFiles.Dispose();
        _filesStagedLabel.Dispose();
        _filesFraction.Dispose();
        _hud.Dispose();
        _cheatsheetOpen.Dispose();
        _cursor.Dispose();
        _marks.Dispose();
        _details.Dispose();
    }
}
