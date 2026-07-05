using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
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

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The body of the Local Changes view: two file-list panels (unstaged / staged) above a
/// diff pane, with a draggable splitter between them. The panels are wired to a
/// <see cref="LocalChangesViewModel"/>'s observable state and forward stage
/// / unstage clicks and row selection back to the VM. Selection is owned by the VM
/// (one <see cref="Selection"/> for both sides), so the panels are stateless
/// w.r.t. selection — rows highlight reactively against the shared selection, and the
/// diff view's target tracks <c>SelectedTarget</c> directly.
/// </summary>
internal sealed class LocalChangesContentView : ContainerView
{
    private readonly LocalChangesPanel _unstagedPanel;
    private readonly LocalChangesPanel _stagedPanel;
    private readonly TextView _placeholder;
    private readonly FlexRowView _errorActions;
    private readonly FlexColumnView _placeholderHost;
    private readonly RectView _centerContainer;
    private readonly View _diffView;
    private readonly VerticalSplitContainer _snapshotContainer;
    private readonly LocalChangesSubmoduleSection _submoduleSection;
    private readonly BorderLayoutView _topHalf;

    private readonly State<Selection> _selection = new(Selection.Empty);
    private readonly LocalChangesViewModel _vm;
    private readonly ListArrowKbmController _arrowController;
    private readonly ILocalizationService _loc;
    private string? _rawPlaceholder;

    // The snapshot panels fade up as a repo's working tree arrives from a placeholder; the
    // placeholder host blooms in (ease-in) so a fast load swaps it out before "Loading…" registers.
    private readonly Tween _enterTween;
    private readonly Tween _placeholderTween;

    public LocalChangesContentView(Context ctx, LocalChangesViewModel vm)
    {
        _vm = vm;
        _loc = ctx.Localization();
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var ticker = ctx.Require<IFrameTicker>();
        _enterTween = new Tween(ticker, Transitions.ContentEnterSeconds, Easings.EaseOutCubic);
        _placeholderTween = new Tween(ticker, Transitions.PlaceholderBloomSeconds, Easings.EaseInCubic);

        var viewModeIcon = Prop.Bind<string?>(() =>
            vm.ViewMode.Value == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List);

        var discardButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.Trash, Command = vm.Discard, Tooltip = L.T(s => s.LocalchangesDiscardSelectedTooltip),
        }.BuildView(ctx);
        var stageSelectedButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ChevronRight, Command = vm.StageSelected, Tooltip = L.T(s => s.LocalchangesStageSelectedTooltip),
        }.BuildView(ctx);
        var stageAllButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ChevronsRight, Command = vm.StageAll, Tooltip = L.T(s => s.LocalchangesStageAllTooltip),
        }.BuildView(ctx);
        var unstageAllButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ChevronsLeft, Command = vm.UnstageAll, Tooltip = L.T(s => s.LocalchangesUnstageAllTooltip),
        }.BuildView(ctx);
        var unstageSelectedButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ChevronLeft, Command = vm.UnstageSelected, Tooltip = L.T(s => s.LocalchangesUnstageSelectedTooltip),
        }.BuildView(ctx);
        var viewModeButtonUnstaged = new LocalChangesHeaderActionButton
        {
            Icon = viewModeIcon, Command = vm.ToggleViewMode, Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        }.BuildView(ctx);
        var viewModeButtonStaged = new LocalChangesHeaderActionButton
        {
            Icon = viewModeIcon, Command = vm.ToggleViewMode, Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        }.BuildView(ctx);

        _unstagedPanel = new LocalChangesPanel(
            ctx,
            s => s.LocalchangesUnstagedPanelTitle,
            DiffSide.Unstaged,
            FileChangesUI.CreateEmptyState(
                ctx,
                LucideIcons.CircleCheck,
                _loc.Strings,
                s => s.LocalchangesUnstagedEmptyTitle,
                s => s.LocalchangesUnstagedEmptyHint),
            _selection,
            OnRowClick,
            [viewModeButtonUnstaged, discardButton, stageSelectedButton, stageAllButton],
            onRowActivated: OnUnstagedRowActivated,
            onEmptyAreaClicked: () => _vm.ClearSelection(),
            onFolderToggle: OnFolderToggle,
            buildContextMenu: BuildUnstagedMenu);
        _stagedPanel = new LocalChangesPanel(
            ctx,
            s => s.LocalchangesStagedPanelTitle,
            DiffSide.Staged,
            FileChangesUI.CreateEmptyState(
                ctx,
                LucideIcons.Inbox,
                _loc.Strings,
                s => s.LocalchangesStagedEmptyTitle,
                s => s.LocalchangesStagedEmptyHint),
            _selection,
            OnRowClick,
            [viewModeButtonStaged, unstageAllButton, unstageSelectedButton],
            onRowActivated: OnStagedRowActivated,
            onEmptyAreaClicked: () => _vm.ClearSelection(),
            onFolderToggle: OnFolderToggle,
            buildContextMenu: BuildStagedMenu);

        _placeholder = new TextView(ctx.Canvas)
        {
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        _placeholder.BindThemedTextColor(theme, s => s.LocalChangesContent.PlaceholderText);

        // Shown only when the placeholder is a status/submodule failure carrying a full error
        // block. Retry re-kicks the loads (which also respawns a dead fsmonitor daemon, the
        // usual cause of a persistent status failure); Show full error opens the block in the
        // scrollable OperationErrorDialog. Status re-reads on every working-tree change, so the
        // failure surfaces inline (one line) and the full block is pulled up on demand rather
        // than auto-popping a modal each poll.
        var retryButton = new ButtonWidget
        {
            Command = new Command(() => _vm.RetryLoad()),
            Children =
            [
                new ButtonIcon { Value = LucideIcons.RefreshCw },
                new ButtonLabel { Value = L.T(s => s.LocalchangesRetryButton) },
            ],
        }.WithTooltip(L.T(s => s.LocalchangesRetryTooltip))
            .WithController<KbmController>()
            .BuildView(ctx);
        var showErrorButton = new ButtonWidget
        {
            Command = new Command(() => _vm.ShowLoadError()),
            Children =
            [
                new ButtonIcon { Value = LucideIcons.TriangleAlert },
                new ButtonLabel { Value = L.T(s => s.LocalchangesShowErrorButton) },
            ],
        }.WithTooltip(L.T(s => s.LocalchangesShowErrorTooltip))
            .WithController<KbmController>()
            .BuildView(ctx);
        _errorActions = new FlexRowView
        {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Gap = Spacing.Md,
            Children = { retryButton, showErrorButton },
        };

        _placeholderHost = new FlexColumnView
        {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Gap = Spacing.Lg,
            Children = { _placeholder },
        };

        _submoduleSection = new LocalChangesSubmoduleSection(
            ctx,
            onStage: path => _vm.StageSubmodulePointer(path),
            onReset: path => _vm.ResetSubmoduleToRecorded(path));

        _diffView = new Provide<DiffViewModel>
        {
            Value = vm.DiffVm,
            Child = new DiffView(),
        }.BuildView(ctx);

        // The diff pane = collapse header on top, diff body below. Collapsing nulls the body so
        // only the header strip remains (the split container pins the pane to header height).
        var diffHeader = new Provide<DiffViewModel>
        {
            Value = vm.DiffVm,
            Child = new DiffPaneHeaderWidget().WithController<KbmController>(),
        }.BuildView(ctx);
        var diffPane = new BorderLayoutView
        {
            North = diffHeader,
            Center = _diffView,
        };
        this.Bind(vm.DiffVm.IsCollapsed, c => diffPane.Center = c ? null : _diffView);

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(theme, s =>
            splitterHovered.Value ? s.LocalChangesContent.SplitterHover : s.LocalChangesContent.SplitterIdle);

        _topHalf = new BorderLayoutView
        {
            Center = BuildContentRow(theme),
        };
        _snapshotContainer = new VerticalSplitContainer(_topHalf, diffPane, splitter, bottomFraction: 2f / 3f);

        splitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.Y,
            _snapshotContainer.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        _centerContainer = new RectView
        {
            Children = { _snapshotContainer },
        };
        _centerContainer.BindThemedBackgroundColor(theme, s => s.LocalChangesContent.ContentBackground);

        AddChildToSelf(_centerContainer);

        _arrowController = new ListArrowKbmController(
            this,
            input,
            (delta, extend) => _vm.MoveSelection(delta, extend),
            expand => _vm.SetCursorFolderExpanded(expand),
            OnActivateSelection,
            OnDeleteSelection);
        _arrowController.OnToggleFullFile = () => _vm.DiffVm.ToggleFullFile();
        this.UseController(input, _arrowController);

        this.Bind(_enterTween.LinearProgress, p => _snapshotContainer.Opacity = p);
        this.Bind(_placeholderTween.Progress, p => _placeholderHost.Opacity = p);
        this.Use(() => _enterTween);
        this.Use(() => _placeholderTween);

        this.Bind(vm.Placeholder, text =>
        {
            _rawPlaceholder = text;
            // Restart here (not inside ShowPlaceholder, which also runs on a locale switch) so the
            // bloom/enter plays only on an actual placeholder↔content transition.
            if (text != null) { ShowPlaceholder(text); _placeholderTween.Restart(); }
            else { AttachSnapshot(); _enterTween.Restart(); }
        });
        // Placeholder copy comes from the VM's state, which doesn't re-emit on a locale switch,
        // so re-resolve the current sentinel against the new catalog when the language changes.
        this.Bind(_loc.Strings, _ =>
        {
            if (_rawPlaceholder != null) ShowPlaceholder(_rawPlaceholder);
        });
        this.Bind(vm.Unstaged, list => _unstagedPanel.SetFiles(list));
        this.Bind(vm.Staged, list => _stagedPanel.SetFiles(list));
        this.Bind(vm.ViewMode, mode =>
        {
            _unstagedPanel.SetViewMode(mode);
            _stagedPanel.SetViewMode(mode);
        });
        this.Bind(vm.UnstagedCollapsed, set => _unstagedPanel.SetCollapsed(set));
        this.Bind(vm.StagedCollapsed, set => _stagedPanel.SetCollapsed(set));
        this.Bind(vm.Selection, sel => _selection.Value = sel);
        this.Bind(vm.Selection, sel =>
        {
            if (sel.Cursor is not { } cursor) return;
            _unstagedPanel.EnsureRowVisible(cursor);
            _stagedPanel.EnsureRowVisible(cursor);
        });
        _snapshotContainer.BindBottomVisible(() => vm.SelectedTarget.Value != null);
        _snapshotContainer.BindBottomCollapsed(vm.DiffVm.IsCollapsed, DiffPaneHeaderWidget.HeaderHeight);

        this.Bind(vm.DriftedSubmodules, drift =>
        {
            _submoduleSection.SetDrift(drift);
            _topHalf.North = drift.Count > 0 ? _submoduleSection : null;
        });
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
        if (_vm.Selection.Value.Count == 0) _vm.MoveSelection(+1, false);
    }

    // Maps the VM's placeholder sentinels (English-keyed consts on LocalChangesState) to the
    // active catalog. A hard load error carries raw git output, which is passed through verbatim.
    private string Localize(string text) => text switch
    {
        LocalChangesState.OpenRepoPlaceholder => _loc.Strings.Value.LocalchangesNoRepo,
        LocalChangesState.LoadingPlaceholder => _loc.Strings.Value.CommonLoading,
        _ => text,
    };

    private void ShowPlaceholder(string text)
    {
        _placeholder.Text = Localize(text);

        // Read the detail straight off the VM rather than a cached field. Current because the
        // VM declares the LoadErrorDetail slice before Placeholder (slices notify in declaration
        // order), so it has recomputed by the time the Placeholder bind lands here.
        var hasDetail = !string.IsNullOrEmpty(_vm.LoadErrorDetail.Value);
        var attached = _placeholderHost.Children.Contains(_errorActions);
        if (hasDetail && !attached) _placeholderHost.Children.Add(_errorActions);
        else if (!hasDetail && attached) _placeholderHost.Children.Remove(_errorActions);

        _centerContainer.Children.Clear();
        _centerContainer.Children.Add(_placeholderHost);
    }

    private void AttachSnapshot()
    {
        _centerContainer.Children.Clear();
        _centerContainer.Children.Add(_snapshotContainer);
    }

    private View BuildContentRow(IThemeService<Theming.ThemeStyles> theme)
    {
        var divider = new RectView { Width = 1 };
        divider.BindThemedBackgroundColor(theme, s => s.LocalChangesContent.ColumnDivider);
        return new TransferListRow(_unstagedPanel, divider, _stagedPanel);
    }

    private void OnRowClick(FileRow row, InputModifiers modifiers)
    {
        // Single-click selects any row — files and folders are independent items. A folder's
        // expand/collapse lives on the chevron (handled before this in the panel),
        // double-click, and the Left/Right arrow keys.
        _vm.SelectRow(row.Ref, modifiers);
        _arrowController.TakeFocus();
    }

    private void OnFolderToggle(FileRow row) => _vm.ToggleFolder(row.Side, row.FullPath);

    // Enter stages the selection from the unstaged side and unstages it from the staged
    // side — mirroring the double-click activation. The commands are side-gated, so an
    // empty or wrong-side selection is a no-op.
    private void OnActivateSelection()
    {
        switch (_vm.Selection.Value.Side)
        {
            case DiffSide.Unstaged: _vm.StageSelected.Execute(); break;
            case DiffSide.Staged: _vm.UnstageSelected.Execute(); break;
        }
    }

    // Delete discards the unstaged selection (routes through the confirm dialog). Staged
    // rows have nothing to discard, so it's a no-op there.
    private void OnDeleteSelection()
    {
        if (_vm.Selection.Value.Side == DiffSide.Unstaged) _vm.Discard.Execute();
    }

    // Double-click stages/unstages a file. Folders toggle via the chevron and arrow keys
    // only — toggling here too would fight the chevron's per-click toggle and flip the
    // folder an unpredictable number of times.
    private void OnUnstagedRowActivated(FileRow row)
    {
        if (row.Kind == FileRowKind.File) _vm.Stage(row.Files);
    }

    private void OnStagedRowActivated(FileRow row)
    {
        if (row.Kind == FileRowKind.File) _vm.Unstage(row.Files);
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildUnstagedMenu(FileRow? target)
    {
        var s = _loc.Strings.Value;
        var items = new List<RepoBarContextMenu.Item>();
        if (target != null)
        {
            var paths = ResolveTargetPaths(target);
            var n = paths.Count;
            var conflicted = _vm.ConflictedAmong(paths);
            if (conflicted.Count > 0)
            {
                items.Add(new RepoBarContextMenu.Item(
                    s.FilesMarkResolved(conflicted.Count),
                    () => _vm.MarkResolved(conflicted),
                    LucideIcons.CheckSquare));
            }
            items.Add(new RepoBarContextMenu.Item(
                s.FilesStage(n),
                () => _vm.Stage(paths),
                LucideIcons.ChevronRight,
                Shortcut: "Enter"));
            items.Add(new RepoBarContextMenu.Item(
                s.FilesDiscard(n),
                () => _vm.RequestDiscard(paths),
                LucideIcons.Trash,
                Shortcut: "Delete"));
            items.Add(new RepoBarContextMenu.Item(
                s.FilesStash(n),
                () => _vm.StashSelected(paths),
                LucideIcons.Stash));
            AppendFileUtilityItems(items, target);
            items.Add(RepoBarContextMenu.Separator);
        }
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesStageAllMenu,
            () => _vm.StageAll.Execute(),
            LucideIcons.ChevronsRight,
            Enabled: _vm.StageAll.CanExecute.Value));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesDiscardAllMenu,
            () => _vm.DiscardAll.Execute(),
            LucideIcons.Trash,
            Enabled: _vm.DiscardAll.CanExecute.Value));
        AppendExpandCollapseItems(items, DiffSide.Unstaged);
        return items;
    }

    // In tree view, offer whole-side expand/collapse alongside the "All" actions. Hidden in
    // flat view, where there are no folders to fold.
    private void AppendExpandCollapseItems(List<RepoBarContextMenu.Item> items, DiffSide side)
    {
        if (_vm.ViewMode.Value != FileViewMode.Tree) return;
        var s = _loc.Strings.Value;
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.CommonExpandAll, () => _vm.ExpandAllFolders(side), LucideIcons.ChevronDown));
        items.Add(new RepoBarContextMenu.Item(
            s.CommonCollapseAll, () => _vm.CollapseAllFolders(side), LucideIcons.ChevronRight));
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildStagedMenu(FileRow? target)
    {
        var items = new List<RepoBarContextMenu.Item>();
        if (target != null)
        {
            var paths = ResolveTargetPaths(target);
            var n = paths.Count;
            items.Add(new RepoBarContextMenu.Item(
                _loc.Strings.Value.FilesUnstage(n),
                () => _vm.Unstage(paths),
                LucideIcons.ChevronLeft,
                Shortcut: "Enter"));
            AppendFileUtilityItems(items, target);
            items.Add(RepoBarContextMenu.Separator);
        }
        items.Add(new RepoBarContextMenu.Item(
            _loc.Strings.Value.LocalchangesUnstageAllMenu,
            () => _vm.UnstageAll.Execute(),
            LucideIcons.ChevronsLeft,
            Enabled: _vm.UnstageAll.CanExecute.Value));
        AppendExpandCollapseItems(items, DiffSide.Staged);
        return items;
    }

    // Copy acts on the resolved selection; open-folder / terminal target the clicked row
    // (the folder itself for a folder row, otherwise the file).
    private void AppendFileUtilityItems(List<RepoBarContextMenu.Item> items, FileRow target)
    {
        var s = _loc.Strings.Value;
        var paths = ResolveTargetPaths(target);
        var representative = target.Kind == FileRowKind.Folder ? target.FullPath : target.File!.Path;
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyPathMenu, () => _vm.CopyPaths(paths), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyFullPathMenu, () => _vm.CopyAbsolutePaths(paths), LucideIcons.Copy));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesCopyFileNameMenu, () => _vm.CopyFileNames(paths), LucideIcons.Copy));
        items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesOpenContainingFolderMenu, () => _vm.OpenContainingFolder(representative), LucideIcons.FolderOpen));
        items.Add(new RepoBarContextMenu.Item(
            s.LocalchangesOpenInTerminalMenu, () => _vm.OpenInTerminal(representative), LucideIcons.SquareTerminal));
    }

    // Right-clicking a row that's part of the current selection acts on the whole
    // selection's files; right-clicking any other row acts on just that row's files
    // (every file under a folder).
    private IReadOnlyList<string> ResolveTargetPaths(FileRow target)
    {
        var selection = _vm.Selection.Value;
        return selection.ContainsRow(target.Ref)
            ? selection.PathsOn(target.Side)
            : target.Files;
    }

}
