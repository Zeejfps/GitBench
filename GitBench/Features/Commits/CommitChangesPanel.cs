using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Input;
using GitBench.Localization;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// The "Changes" file-list panel of a commit-details surface, bound to a
/// <see cref="CommitDetailsViewModel"/>: clicking (or arrow-keying) a row activates the file.
/// Activation defaults to opening the file's diff tab through the view model; a host can override
/// both the activation and the highlighted row — the review window routes activation to
/// scroll-to-file and highlights the file currently read. Reused by the History pane (top of its
/// split) and the review window (left column).
///
/// Single-select by default. A host that supplies <see cref="OnSelect"/> takes over the row gestures
/// with their modifiers (Ctrl/Cmd toggle, Shift range) and paints its own <see cref="SelectedPaths"/>
/// set; the review window does this so a group of files can be marked Viewed at once.
/// </summary>
internal sealed record CommitChangesPanel : IWidget
{
    /// <summary>Overrides the highlighted row; null follows the view model's active tab.</summary>
    public IReadable<string?>? SelectedPath { get; init; }

    /// <summary>Every selected row, for a multi-select host; null highlights only <see cref="SelectedPath"/>.</summary>
    public IReadable<IReadOnlySet<string>>? SelectedPaths { get; init; }

    /// <summary>The row arrow keys step on from; null steps from <see cref="SelectedPath"/>.</summary>
    public IReadable<string?>? CursorPath { get; init; }

    /// <summary>Overrides what activating a file does; null opens its diff tab.</summary>
    public Action<string>? OnActivate { get; init; }

    /// <summary>
    /// Takes over row gestures — the clicked (or arrow-resolved) path, its modifiers, and the file
    /// rows the tree currently shows. Null falls back to a plain <see cref="OnActivate"/>.
    /// </summary>
    public Action<string, InputModifiers, IReadOnlyList<string>>? OnSelect { get; init; }

    /// <summary>Ctrl/Cmd+A over the visible file rows; null leaves the key unhandled.</summary>
    public Action<IReadOnlyList<string>>? OnSelectAll { get; init; }

    /// <summary>
    /// Enter on the current row, folder or file alike — the host resolves what "the current row" means.
    /// Null makes Enter a no-op (the tree still swallows it).
    /// </summary>
    public Action? OnActivateSelection { get; init; }

    /// <summary>Buttons appended to the header toolbar, after the view-mode toggle.</summary>
    public IReadOnlyList<IWidget>? HeaderActions { get; init; }

    /// <summary>Optional right-click handler for file rows; null means no context menu. The row stays
    /// highlighted until the returned menu closes.</summary>
    public Func<FileChange, PointF, IOpenedContextMenu?>? OnFileContextMenu { get; init; }

    /// <summary>
    /// Optional right-click handler for folder rows, receiving the folder's path and every file
    /// beneath it; null means no context menu. Only tree mode ever shows folder rows.
    /// </summary>
    public Func<string, IReadOnlyList<string>, PointF, IOpenedContextMenu?>? OnFolderContextMenu { get; init; }

    /// <summary>
    /// Optional right-click handler for the space below the last row, where there is no file to act
    /// on; null means no context menu there.
    /// </summary>
    public Func<PointF, IOpenedContextMenu?>? OnEmptyContextMenu { get; init; }

    /// <summary>Overrides the view shown when the list is empty; null keeps the section's default text.</summary>
    public Func<Context, View>? EmptyState { get; init; }

    /// <summary>
    /// Whether this panel is the top of the content panel rather than a column inside one. When it
    /// is, the header drops its own top rule and wears the panel's fill, so the tab strip's join —
    /// the one the active tab breaks — is the only line there.
    /// </summary>
    public bool HeadsContentPanel { get; init; }

    public View BuildView(Context ctx) => new CommitChangesPanelView(this, ctx, ctx.Require<CommitDetailsViewModel>());
}

internal sealed class CommitChangesPanelView : ContainerView
{
    private readonly IRepoRegistry? _registry;
    private readonly ListArrowKbmController _arrowController;
    private readonly State<IReadOnlyList<FileChange>> _files = new(Array.Empty<FileChange>());
    private readonly State<string?> _loadedSha = new(null);
    private readonly State<FileRowRef?> _scrollTo = new(null);
    private readonly CommitDetailsViewModel _vm;

    public CommitChangesPanelView(CommitChangesPanel props, Context ctx, CommitDetailsViewModel vm)
    {
        _vm = vm;
        _registry = ctx.Get<IRepoRegistry>();
        var input = ctx.Require<InputSystem>();
        var selection = props.SelectedPath ?? vm.SelectedPath;
        var activate = props.OnActivate ?? vm.SelectFile;
        var onSelect = props.OnSelect;
        var cursor = props.CursorPath ?? selection;

        var viewModeButton = new LocalChangesHeaderActionButton
        {
            Icon = Prop.Bind<string?>(() =>
                vm.ViewMode.Value == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List),
            Command = vm.ToggleViewMode,
            Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        };
        var headerActions = new List<IWidget> { viewModeButton };
        if (props.HeaderActions is { } extra) headerActions.AddRange(extra);

        // Up/Down arrow navigation over the rows, mirroring the local-changes panels: folder rows are
        // cursor stops too, so the keyboard can fold them with Left/Right. Stepping onto a file
        // resumes the normal selection gesture (Shift-extend continues from the cursor) and hands the
        // folder cursor back; stepping onto a folder leaves the file selection where it was.
        _arrowController = new ListArrowKbmController(
            this,
            input,
            ctx.KeyMap(),
            (delta, shift) =>
            {
                var rows = Rows();
                if (rows.Count == 0) return;
                var current = vm.CursorFolder.Value is { } folder
                    ? IndexOf(rows, folder, isFolder: true)
                    : cursor.Value is { } path ? IndexOf(rows, path, isFolder: false) : -1;
                var next = rows[ListNavigation.NextIndex(rows.Count, current, delta)];
                if (next is FileRow.Folder)
                {
                    vm.SetCursorFolder(next.FullPath);
                    _scrollTo.Value = next.Ref;
                    return;
                }
                vm.SetCursorFolder(null);
                Gesture(next.FullPath, shift ? InputModifiers.Shift : InputModifiers.None);
                _scrollTo.Value = next.Ref;
            },
            vm.SetCursorFolderExpanded,
            () => props.OnActivateSelection?.Invoke(),
            () => { });
        _arrowController.OnToggleFullFile = () => vm.ActiveDiff?.ToggleFullFile();
        if (props.OnSelectAll is { } selectAll)
            _arrowController.OnSelectAll = () => selectAll(VisibleFilePaths());
        this.UseController(input, _arrowController);

        AddChildToSelf(new FileRowList
        {
            Title = "Changes",
            Side = DiffSide.Commit,
            Files = Prop.Bind(_files),
            ViewMode = Prop.Bind(vm.ViewMode),
            Collapsed = Prop.Bind(vm.CollapsedFolders),
            Highlight = Prop.Bind(() => Highlight(vm.CursorFolder.Value, selection.Value, props.SelectedPaths?.Value)),
            ScrollTo = Prop.Bind(_scrollTo),
            // A reload of the same commit (the working-tree review re-pushes its list on every index
            // op) refreshes in place; only a genuinely new selection scrolls to top.
            ContentKey = _loadedSha.Bind(object? (sha) => sha),
            EmptyState = new Raw
            {
                View = props.EmptyState?.Invoke(ctx) ?? FileChangesUI.CreateEmptyPlaceholder(ctx, "(none)"),
            },
            HeaderActions = headerActions,
            HeadsContentPanel = props.HeadsContentPanel,
            OnRowClick = (row, modifiers) =>
            {
                switch (row)
                {
                    case FileRow.Folder:
                        vm.SetCursorFolder(row.FullPath);
                        break;
                    case FileRow.File { Change: { Status: FileChangeStatus.Submodule, PointerChange: { } pointer } change }:
                        ActivateSubmoduleAndJump(change.Path, pointer);
                        return;
                    case FileRow.File:
                        vm.SetCursorFolder(null);
                        Gesture(row.FullPath, modifiers);
                        break;
                }
                _arrowController.TakeFocus();
            },
            OnFolderToggle = row => vm.ToggleFolder(row.FullPath),
            ContextMenu = (row, point) => row switch
            {
                null => props.OnEmptyContextMenu?.Invoke(point),
                FileRow.Folder => props.OnFolderContextMenu?.Invoke(row.FullPath, row.Files, point),
                FileRow.File { Change: var file } => props.OnFileContextMenu?.Invoke(file, point),
                _ => null,
            },
        }.BuildView(ctx));

        // A multi-select host owns the gesture (modifiers, ranges); everyone else just activates.
        void Gesture(string path, InputModifiers modifiers)
        {
            if (onSelect != null) onSelect(path, modifiers, VisibleFilePaths());
            else activate(path);
        }

        this.Bind(selection, path =>
        {
            if (path != null) _scrollTo.Value = new FileRowRef(DiffSide.Commit, path, false);
        });
        // Loaded fills the list; a placeholder clears it. Loading deliberately keeps the previous
        // list up (stale-while-revalidate), matching the details host's skeleton rules.
        this.Bind(vm.RenderState, state =>
        {
            switch (state)
            {
                case CommitDetailsRenderState.Loaded l:
                    _files.Value = l.Details.Files;
                    _loadedSha.Value = l.Details.Sha;
                    break;
                case CommitDetailsRenderState.Placeholder:
                    _files.Value = Array.Empty<FileChange>();
                    _loadedSha.Value = null;
                    break;
            }
        });
    }

    // The bar sits on the cursor folder when there is one, else on the lead file. Every selected file
    // fills; only the lead wears the accent, so it stays the one the eye (and the diff view) is
    // anchored to.
    private static RowHighlight Highlight(string? cursorFolder, string? lead, IReadOnlySet<string>? selectedPaths)
    {
        var selected = new HashSet<FileRowRef>();
        var accented = new HashSet<FileRowRef>();
        FileRowRef? bar = null;
        if (lead != null)
        {
            var leadRef = new FileRowRef(DiffSide.Commit, lead, false);
            bar = leadRef;
            accented.Add(leadRef);
            if (selectedPaths == null) selected.Add(leadRef);
        }
        if (selectedPaths != null)
            foreach (var path in selectedPaths)
                selected.Add(new FileRowRef(DiffSide.Commit, path, false));
        if (cursorFolder != null)
        {
            var folderRef = new FileRowRef(DiffSide.Commit, cursorFolder, true);
            bar = folderRef;
            selected.Add(folderRef);
        }
        return new RowHighlight(bar, selected, accented);
    }

    // The rows the list currently shows — the universe an arrow key or a Shift-range gesture moves
    // over. Rows under a collapsed folder are not in it at all, so they are skipped for free.
    private IReadOnlyList<FileRow> Rows()
        => FileTreeBuilder.BuildRows(_files.Value, DiffSide.Commit, _vm.ViewMode.Value, _vm.CollapsedFolders.Value);

    private IReadOnlyList<string> VisibleFilePaths()
    {
        var paths = new List<string>();
        foreach (var row in Rows())
            if (row is FileRow.File) paths.Add(row.FullPath);
        return paths;
    }

    private static int IndexOf(IReadOnlyList<FileRow> rows, string fullPath, bool isFolder)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].FullPath == fullPath && (rows[i] is FileRow.Folder) == isFolder) return i;
        return -1;
    }

    // Compare-by-relative-path: a submodule's absolute path can vary across worktrees, so we resolve
    // relative to the active repo's parent and match by GetFullPath.
    private void ActivateSubmoduleAndJump(string submodulePath, SubmodulePointerChange change)
    {
        if (_registry is not { } registry) return;
        if (registry.Active.Value is not { } active) return;

        var primaryId = active.PrimaryId;
        var parentPath = active.IsPrimary
            ? active.Path
            : (FindParentPath(registry, primaryId) ?? active.Path);
        var target = Path.GetFullPath(Path.Combine(parentPath, submodulePath));

        foreach (var r in registry.GetSubmodules(primaryId))
        {
            if (string.Equals(Path.GetFullPath(r.Path), target, PathComparison))
            {
                if (!r.IsMissing) registry.SetActive(r.Id);
                return;
            }
        }
    }

    private static string? FindParentPath(IRepoRegistry registry, Guid primaryId)
    {
        foreach (var r in registry.Repos)
            if (r.Id == primaryId) return r.Path;
        return null;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
