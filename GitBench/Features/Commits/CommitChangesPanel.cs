using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
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

    /// <summary>Buttons appended to the header toolbar, after the view-mode toggle.</summary>
    public IReadOnlyList<IWidget>? HeaderActions { get; init; }

    /// <summary>Optional right-click handler for file rows; null means no context menu.</summary>
    public Action<FileChange, PointF>? OnFileContextMenu { get; init; }

    /// <summary>
    /// Optional right-click handler for folder rows, receiving the folder's path and every file
    /// beneath it; null means no context menu. Only tree mode ever shows folder rows.
    /// </summary>
    public Action<string, IReadOnlyList<string>, PointF>? OnFolderContextMenu { get; init; }

    /// <summary>
    /// Optional right-click handler for the space below the last row, where there is no file to act
    /// on; null means no context menu there.
    /// </summary>
    public Action<PointF>? OnEmptyContextMenu { get; init; }

    /// <summary>Overrides the view shown when the list is empty; null keeps the section's default text.</summary>
    public Func<Context, View>? EmptyState { get; init; }

    public View BuildView(Context ctx) => new CommitChangesPanelView(this, ctx, ctx.Require<CommitDetailsViewModel>());
}

internal sealed class CommitChangesPanelView : ContainerView
{
    private readonly FileChangesSection _changesSection;
    private readonly ListArrowKbmController _arrowController;
    private readonly State<string?> _selectedPath = new(null);
    private string? _loadedSha;

    public CommitChangesPanelView(CommitChangesPanel props, Context ctx, CommitDetailsViewModel vm)
    {
        var input = ctx.Require<InputSystem>();
        var selection = props.SelectedPath ?? vm.SelectedPath;
        var activate = props.OnActivate ?? vm.SelectFile;
        var onSelect = props.OnSelect;
        var cursor = props.CursorPath ?? selection;

        var viewModeIcon = Prop.Bind<string?>(() =>
            vm.ViewMode.Value == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List);
        var viewModeButton = new LocalChangesHeaderActionButton
        {
            Icon = viewModeIcon,
            Command = vm.ToggleViewMode,
            Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        }.BuildView(ctx);

        var headerActions = new List<View> { viewModeButton };
        if (props.HeaderActions is { } extra)
            foreach (var action in extra) headerActions.Add(action.BuildView(ctx));

        _changesSection = new FileChangesSection(
            ctx,
            "Changes",
            selectedPath: _selectedPath,
            onRowClicked: (f, modifiers) =>
            {
                vm.SetCursorFolder(null);
                Gesture(f.Path, modifiers);
                _arrowController.TakeFocus();
            },
            headerActions: headerActions,
            onFileContextMenu: props.OnFileContextMenu,
            selectedPaths: props.SelectedPaths,
            onFolderContextMenu: props.OnFolderContextMenu,
            onEmptyContextMenu: props.OnEmptyContextMenu,
            emptyView: props.EmptyState?.Invoke(ctx),
            onToggleFolder: vm.ToggleFolder,
            onFolderClicked: folderPath =>
            {
                vm.SetCursorFolder(folderPath);
                _arrowController.TakeFocus();
            });
        AddChildToSelf(_changesSection);

        // Up/Down arrow navigation over the rows, mirroring the local-changes panels: folder rows are
        // cursor stops too, so the keyboard can fold them with Left/Right. Stepping onto a file
        // resumes the normal selection gesture (Shift-extend continues from the cursor) and hands the
        // folder cursor back; stepping onto a folder leaves the file selection where it was.
        _arrowController = new ListArrowKbmController(
            this,
            input,
            (delta, shift) =>
            {
                var next = _changesSection.NextRow(vm.CursorFolder.Value, cursor.Value, delta);
                if (next == null) return;
                if (next.Kind == FileRowKind.Folder)
                {
                    vm.SetCursorFolder(next.FullPath);
                    _changesSection.EnsureFolderVisible(next.FullPath);
                    return;
                }
                vm.SetCursorFolder(null);
                Gesture(next.File!.Path, shift ? InputModifiers.Shift : InputModifiers.None);
                _changesSection.EnsureRowVisible(next.File!.Path);
            },
            vm.SetCursorFolderExpanded,
            () => { },
            () => { });
        _arrowController.OnToggleFullFile = () => vm.ActiveDiff?.ToggleFullFile();
        if (props.OnSelectAll is { } selectAll)
            _arrowController.OnSelectAll = () => selectAll(_changesSection.VisibleFilePaths);
        this.UseController(input, _arrowController);

        // A multi-select host owns the gesture (modifiers, ranges); everyone else just activates.
        void Gesture(string path, InputModifiers modifiers)
        {
            if (onSelect != null) onSelect(path, modifiers, _changesSection.VisibleFilePaths);
            else activate(path);
        }

        this.Bind(vm.ViewMode, _changesSection.SetViewMode);
        this.Bind(vm.CollapsedFolders, _changesSection.SetCollapsedFolders);
        this.Bind(vm.CursorFolder, _changesSection.SetCursorFolder);
        this.Bind(selection, path =>
        {
            _selectedPath.Value = path;
            if (path != null) _changesSection.EnsureRowVisible(path);
        });
        // Loaded fills the list; a placeholder clears it. Loading deliberately keeps the previous
        // list up (stale-while-revalidate), matching the details host's skeleton rules. A reload of
        // the same details identity (the working-tree review re-pushes its list on every index op)
        // refreshes in place, keeping the scroll; only a genuinely new selection scrolls to top.
        this.Bind(vm.RenderState, state =>
        {
            switch (state)
            {
                case CommitDetailsRenderState.Loaded l:
                    _changesSection.SetFiles(l.Details.Files, preserveScroll: l.Details.Sha == _loadedSha);
                    _loadedSha = l.Details.Sha;
                    _changesSection.SetReviewSha(l.Details.Sha);
                    break;
                case CommitDetailsRenderState.Placeholder:
                    _changesSection.SetFiles(Array.Empty<FileChange>());
                    _loadedSha = null;
                    _changesSection.SetReviewSha(null);
                    break;
            }
        });
    }
}
