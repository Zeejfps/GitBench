using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
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
/// <see cref="CommitDetailsViewModel"/>: clicking (or arrow-keying) a row opens the file's diff tab
/// through the view model. Reused wherever that surface appears — the History pane stacks it above
/// the tabbed diff region, the review window puts it in the left column.
/// </summary>
internal sealed record CommitChangesPanel : IWidget
{
    public View BuildView(Context ctx) => new CommitChangesPanelView(ctx, ctx.Require<CommitDetailsViewModel>());
}

internal sealed class CommitChangesPanelView : ContainerView
{
    private readonly FileChangesSection _changesSection;
    private readonly ListArrowKbmController _arrowController;
    private readonly State<string?> _selectedPath = new(null);

    public CommitChangesPanelView(Context ctx, CommitDetailsViewModel vm)
    {
        var input = ctx.Require<InputSystem>();

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
                vm.SelectFile(f.Path);
                _arrowController.TakeFocus();
            },
            headerActions: [viewModeButton]);
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
                if (next != null) vm.SelectFile(next);
            },
            _ => { },
            () => { },
            () => { });
        _arrowController.OnToggleFullFile = () => vm.ActiveDiff?.ToggleFullFile();
        this.UseController(input, _arrowController);

        this.Bind(vm.ViewMode, _changesSection.SetViewMode);
        this.Bind(vm.SelectedPath, path =>
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
