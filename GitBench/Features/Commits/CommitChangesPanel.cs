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
/// </summary>
internal sealed record CommitChangesPanel : IWidget
{
    /// <summary>Overrides the highlighted row; null follows the view model's active tab.</summary>
    public IReadable<string?>? SelectedPath { get; init; }

    /// <summary>Overrides what activating a file does; null opens its diff tab.</summary>
    public Action<string>? OnActivate { get; init; }

    /// <summary>Optional right-click handler for file rows; null means no context menu.</summary>
    public Action<FileChange, PointF>? OnFileContextMenu { get; init; }

    public View BuildView(Context ctx) =>
        new CommitChangesPanelView(ctx, ctx.Require<CommitDetailsViewModel>(), SelectedPath, OnActivate, OnFileContextMenu);
}

internal sealed class CommitChangesPanelView : ContainerView
{
    private readonly FileChangesSection _changesSection;
    private readonly ListArrowKbmController _arrowController;
    private readonly State<string?> _selectedPath = new(null);

    public CommitChangesPanelView(
        Context ctx,
        CommitDetailsViewModel vm,
        IReadable<string?>? selectedPath = null,
        Action<string>? onActivate = null,
        Action<FileChange, PointF>? onFileContextMenu = null)
    {
        var input = ctx.Require<InputSystem>();
        var selection = selectedPath ?? vm.SelectedPath;
        var activate = onActivate ?? vm.SelectFile;

        var viewModeIcon = Prop.Bind<string?>(() =>
            vm.ViewMode.Value == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List);
        var viewModeButton = new LocalChangesHeaderActionButton
        {
            Icon = viewModeIcon,
            Command = vm.ToggleViewMode,
            Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        }.BuildView(ctx);

        _changesSection = new FileChangesSection(
            ctx,
            "Changes",
            selectedPath: _selectedPath,
            onRowClicked: f =>
            {
                activate(f.Path);
                _arrowController.TakeFocus();
            },
            headerActions: [viewModeButton],
            onFileContextMenu: onFileContextMenu);
        AddChildToSelf(_changesSection);

        // Up/Down arrow navigation over the file rows, mirroring the local-changes panels.
        // Single-select with no stage/discard actions; arrows step through the visible file
        // rows only (folder rows are toggled by mouse, skipped by the keyboard).
        _arrowController = new ListArrowKbmController(
            this,
            input,
            (delta, _) =>
            {
                var next = _changesSection.NextFilePath(_selectedPath.Value, delta);
                if (next != null) activate(next);
            },
            _ => { },
            () => { },
            () => { });
        _arrowController.OnToggleFullFile = () => vm.ActiveDiff?.ToggleFullFile();
        this.UseController(input, _arrowController);

        this.Bind(vm.ViewMode, _changesSection.SetViewMode);
        this.Bind(selection, path =>
        {
            _selectedPath.Value = path;
            if (path != null) _changesSection.EnsureRowVisible(path);
        });
        // Loaded fills the list; a placeholder clears it. Loading deliberately keeps the previous
        // list up (stale-while-revalidate), matching the details host's skeleton rules.
        this.Bind(vm.RenderState, state =>
        {
            switch (state)
            {
                case CommitDetailsRenderState.Loaded l:
                    _changesSection.SetFiles(l.Details.Files);
                    _changesSection.SetReviewSha(l.Details.Sha);
                    break;
                case CommitDetailsRenderState.Placeholder:
                    _changesSection.SetFiles(Array.Empty<FileChange>());
                    _changesSection.SetReviewSha(null);
                    break;
            }
        });
    }
}
