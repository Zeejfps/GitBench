using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// The body of the Local Changes view: two file-list panels (unstaged / staged) above a
/// diff pane, with a draggable splitter between them. <see cref="Bind"/> wires the
/// panels to a <see cref="LocalChangesViewModel"/>'s observable state and forwards stage
/// / unstage clicks and row selection back to the VM. Selection is owned by the VM
/// (one <see cref="GitGui.Selection"/> for both sides), so the panels are stateless
/// w.r.t. selection — rows highlight reactively against the shared selection, and the
/// diff view's target tracks <c>SelectedTarget</c> directly.
/// </summary>
internal sealed class LocalChangesContentView : MultiChildView, IBind<LocalChangesViewModel>
{
    private readonly LocalChangesPanel _unstagedPanel;
    private readonly LocalChangesPanel _stagedPanel;
    private readonly TextView _placeholder;
    private readonly RectView _centerContainer;
    private readonly DiffView _diffView;
    private readonly VerticalSplitContainer _snapshotContainer;
    private readonly LocalChangesHeaderActionButton _discardButton;
    private readonly LocalChangesHeaderActionButton _stageSelectedButton;
    private readonly LocalChangesHeaderActionButton _stageAllButton;
    private readonly LocalChangesHeaderActionButton _unstageAllButton;
    private readonly LocalChangesHeaderActionButton _unstageSelectedButton;
    private readonly LocalChangesHeaderActionButton _viewModeButtonUnstaged;
    private readonly LocalChangesHeaderActionButton _viewModeButtonStaged;
    private readonly LocalChangesSubmoduleSection _submoduleSection;
    private readonly BorderLayoutView _topHalf;
    
    private readonly State<Selection> _selection = new(Selection.Empty);
    private LocalChangesViewModel? _vm;
    private readonly LocalChangesArrowKbmController _arrowController;

    public LocalChangesContentView()
    {
        _discardButton = new LocalChangesHeaderActionButton(
            LucideIcons.Trash, tooltip: "Discard selected changes");
        _stageSelectedButton = new LocalChangesHeaderActionButton(
            LucideIcons.ChevronRight, tooltip: "Stage selected");
        _stageAllButton = new LocalChangesHeaderActionButton(
            LucideIcons.ChevronsRight, tooltip: "Stage all");
        _unstageAllButton = new LocalChangesHeaderActionButton(
            LucideIcons.ChevronsLeft, tooltip: "Unstage all");
        _unstageSelectedButton = new LocalChangesHeaderActionButton(
            LucideIcons.ChevronLeft, tooltip: "Unstage selected");
        _viewModeButtonUnstaged = new LocalChangesHeaderActionButton(
            LucideIcons.List, onClick: () => _vm?.ToggleViewMode(), tooltip: "Toggle list / tree view");
        _viewModeButtonStaged = new LocalChangesHeaderActionButton(
            LucideIcons.List, onClick: () => _vm?.ToggleViewMode(), tooltip: "Toggle list / tree view");

        _unstagedPanel = new LocalChangesPanel(
            "Unstaged",
            DiffSide.Unstaged,
            "No unstaged changes.",
            _selection,
            OnRowClick,
            [_viewModeButtonUnstaged, _discardButton, _stageSelectedButton, _stageAllButton],
            onRowActivated: OnUnstagedRowActivated,
            onEmptyAreaClicked: () => _vm?.ClearSelection(),
            onFolderToggle: OnFolderToggle,
            buildContextMenu: BuildUnstagedMenu);
        _stagedPanel = new LocalChangesPanel(
            "Staged",
            DiffSide.Staged,
            "No staged changes.",
            _selection,
            OnRowClick,
            [_viewModeButtonStaged, _unstageAllButton, _unstageSelectedButton],
            onRowActivated: OnStagedRowActivated,
            onEmptyAreaClicked: () => _vm?.ClearSelection(),
            onFolderToggle: OnFolderToggle,
            buildContextMenu: BuildStagedMenu);

        _placeholder = new TextView
        {
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _placeholder.BindThemedTextColor(s => s.LocalChangesContent.PlaceholderText);

        _submoduleSection = new LocalChangesSubmoduleSection(
            onStage: path => _vm?.StageSubmodulePointer(path),
            onReset: path => _vm?.ResetSubmoduleToRecorded(path));

        _diffView = new DiffView(); 
        
        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(s =>
            splitterHovered.Value ? s.LocalChangesContent.SplitterHover : s.LocalChangesContent.SplitterIdle);
        
        _topHalf = new BorderLayoutView
        {
            Center = BuildContentRow(),
        };
        _snapshotContainer = new VerticalSplitContainer(_topHalf, _diffView, splitter, bottomFraction: 2f / 3f);

        splitter.UseController(ctx => new SplitterController(
            ctx,
            DragAxis.Y,
            _snapshotContainer.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        _centerContainer = new RectView
        {
            Children = { _snapshotContainer },
        };
        _centerContainer.BindThemedBackgroundColor(s => s.LocalChangesContent.ContentBackground);

        AddChildToSelf(_centerContainer);

        _arrowController = new LocalChangesArrowKbmController(
            this,
            (delta, extend) => _vm?.MoveSelection(delta, extend),
            expand => _vm?.SetCursorFolderExpanded(expand),
            OnActivateSelection,
            OnDeleteSelection);
        this.UseController(_ => _arrowController);
    }

    // Joins the unstaged file list to the shared focus ring as the first stop, ahead of
    // the commit fields. Tab into the list selects the first row when nothing is selected
    // yet so arrow navigation has a visible starting point.
    public void RegisterFocusStops(FocusRing ring)
    {
        var stop = ring.Add(FocusFiles);
        _arrowController.OnTab = () => ring.Next(stop);
        _arrowController.OnShiftTab = () => ring.Previous(stop);
    }

    private void FocusFiles()
    {
        _arrowController.TakeFocus();
        if (_vm != null && _vm.Selection.Value.Count == 0) _vm.MoveSelection(+1, false);
    }

    public void Bind(LocalChangesViewModel vm)
    {
        _vm = vm;
        
        vm.Placeholder.Subscribe(text =>
        {
            if (text != null) ShowPlaceholder(text);
            else AttachSnapshot();
        });
        vm.Unstaged.Subscribe(list => _unstagedPanel.SetFiles(list));
        vm.Staged.Subscribe(list => _stagedPanel.SetFiles(list));
        vm.ViewMode.Subscribe(mode =>
        {
            _unstagedPanel.SetViewMode(mode);
            _stagedPanel.SetViewMode(mode);
            var icon = mode == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List;
            _viewModeButtonUnstaged.SetIcon(icon);
            _viewModeButtonStaged.SetIcon(icon);
        });
        vm.UnstagedCollapsed.Subscribe(set => _unstagedPanel.SetCollapsed(set));
        vm.StagedCollapsed.Subscribe(set => _stagedPanel.SetCollapsed(set));
        _selection.BindTo(vm.Selection);
        vm.Selection.Subscribe(sel =>
        {
            if (sel.Cursor is not { } cursor) return;
            _unstagedPanel.EnsureRowVisible(cursor);
            _stagedPanel.EnsureRowVisible(cursor);
        });
        _diffView.Bind(vm.DiffVm);
        _snapshotContainer.BindBottomVisible(() => vm.SelectedTarget.Value != null);
        _snapshotContainer.BindBottomCollapsed(_diffView.IsCollapsed, DiffView.HeaderHeight);

        _discardButton.BindCommand(vm.Discard);
        _stageSelectedButton.BindCommand(vm.StageSelected);
        _stageAllButton.BindCommand(vm.StageAll);
        _unstageAllButton.BindCommand(vm.UnstageAll);
        _unstageSelectedButton.BindCommand(vm.UnstageSelected);

        vm.DriftedSubmodules.Subscribe(drift =>
        {
            _submoduleSection.SetDrift(drift);
            _topHalf.North = drift.Count > 0 ? _submoduleSection : null;
        });
    }

    private void ShowPlaceholder(string text)
    {
        _placeholder.Text = text;
        _centerContainer.Children.Clear();
        _centerContainer.Children.Add(_placeholder);
    }

    private void AttachSnapshot()
    {
        _centerContainer.Children.Clear();
        _centerContainer.Children.Add(_snapshotContainer);
    }

    private View BuildContentRow()
    {
        var divider = new RectView { Width = 1 };
        divider.BindThemedBackgroundColor(s => s.LocalChangesContent.ColumnDivider);
        return new TransferListRow(_unstagedPanel, divider, _stagedPanel);
    }

    private void OnRowClick(FileRow row, InputModifiers modifiers)
    {
        // Single-click selects any row — files and folders are independent items. A folder's
        // expand/collapse lives on the chevron (handled before this in the panel),
        // double-click, and the Left/Right arrow keys.
        _vm?.SelectRow(row.Ref, modifiers);
        _arrowController.TakeFocus();
    }

    private void OnFolderToggle(FileRow row) => _vm?.ToggleFolder(row.Side, row.FullPath);

    // Enter stages the selection from the unstaged side and unstages it from the staged
    // side — mirroring the double-click activation. The commands are side-gated, so an
    // empty or wrong-side selection is a no-op.
    private void OnActivateSelection()
    {
        switch (_vm?.Selection.Value.Side)
        {
            case DiffSide.Unstaged: _vm.StageSelected.Execute(); break;
            case DiffSide.Staged: _vm.UnstageSelected.Execute(); break;
        }
    }

    // Delete discards the unstaged selection (routes through the confirm dialog). Staged
    // rows have nothing to discard, so it's a no-op there.
    private void OnDeleteSelection()
    {
        if (_vm?.Selection.Value.Side == DiffSide.Unstaged) _vm.Discard.Execute();
    }

    // Double-click stages/unstages a file. Folders toggle via the chevron and arrow keys
    // only — toggling here too would fight the chevron's per-click toggle and flip the
    // folder an unpredictable number of times.
    private void OnUnstagedRowActivated(FileRow row)
    {
        if (row.Kind == FileRowKind.File) _vm?.Stage(row.Files);
    }

    private void OnStagedRowActivated(FileRow row)
    {
        if (row.Kind == FileRowKind.File) _vm?.Unstage(row.Files);
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildUnstagedMenu(FileRow? target)
    {
        if (_vm == null) return [];
        var items = new List<RepoBarContextMenu.Item>();
        if (target != null)
        {
            var paths = ResolveTargetPaths(target);
            var n = paths.Count;
            items.Add(new RepoBarContextMenu.Item(
                n > 1 ? $"Stage {n} Files" : "Stage",
                () => _vm.Stage(paths),
                LucideIcons.ChevronRight,
                Shortcut: "Enter"));
            items.Add(new RepoBarContextMenu.Item(
                n > 1 ? $"Discard {n} Files…" : "Discard…",
                () => _vm.RequestDiscard(paths),
                LucideIcons.Trash,
                Shortcut: "Delete"));
            items.Add(new RepoBarContextMenu.Item(
                n > 1 ? $"Stash {n} Files" : "Stash",
                () => _vm.StashSelected(paths),
                LucideIcons.Stash));
            AppendFileUtilityItems(items, target);
            items.Add(RepoBarContextMenu.Separator);
        }
        items.Add(new RepoBarContextMenu.Item(
            "Stage All",
            () => _vm.StageAll.Execute(),
            LucideIcons.ChevronsRight,
            Enabled: _vm.StageAll.CanExecute.Value));
        items.Add(new RepoBarContextMenu.Item(
            "Discard All…",
            () => _vm.DiscardAll.Execute(),
            LucideIcons.Trash,
            Enabled: _vm.DiscardAll.CanExecute.Value));
        return items;
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildStagedMenu(FileRow? target)
    {
        if (_vm == null) return [];
        var items = new List<RepoBarContextMenu.Item>();
        if (target != null)
        {
            var paths = ResolveTargetPaths(target);
            var n = paths.Count;
            items.Add(new RepoBarContextMenu.Item(
                n > 1 ? $"Unstage {n} Files" : "Unstage",
                () => _vm.Unstage(paths),
                LucideIcons.ChevronLeft,
                Shortcut: "Enter"));
            AppendFileUtilityItems(items, target);
            items.Add(RepoBarContextMenu.Separator);
        }
        items.Add(new RepoBarContextMenu.Item(
            "Unstage All",
            () => _vm.UnstageAll.Execute(),
            LucideIcons.ChevronsLeft,
            Enabled: _vm.UnstageAll.CanExecute.Value));
        return items;
    }

    // Copy acts on the resolved selection; open-folder / terminal target the clicked row
    // (the folder itself for a folder row, otherwise the file).
    private void AppendFileUtilityItems(List<RepoBarContextMenu.Item> items, FileRow target)
    {
        var paths = ResolveTargetPaths(target);
        var representative = target.Kind == FileRowKind.Folder ? target.FullPath : target.File!.Path;
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            "Copy Path", () => _vm!.CopyPaths(paths), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            "Copy Full Path", () => _vm!.CopyAbsolutePaths(paths), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            "Copy File Name", () => _vm!.CopyFileNames(paths), LucideIcons.Copy));
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            "Open Containing Folder", () => _vm!.OpenContainingFolder(representative), LucideIcons.FolderOpen));
        items.Add(new RepoBarContextMenu.Item(
            "Open in Terminal", () => _vm!.OpenInTerminal(representative), LucideIcons.SquareTerminal));
    }

    // Right-clicking a row that's part of the current selection acts on the whole
    // selection's files; right-clicking any other row acts on just that row's files
    // (every file under a folder).
    private IReadOnlyList<string> ResolveTargetPaths(FileRow target)
    {
        var selection = _vm!.Selection.Value;
        return selection.ContainsRow(target.Ref)
            ? selection.PathsOn(target.Side)
            : target.Files;
    }

}
