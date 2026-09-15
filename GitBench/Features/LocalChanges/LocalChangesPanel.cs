using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// One side of the Local Changes split (Unstaged or Staged). Renders a header bar with
/// action buttons, a virtualized list of file rows, and an empty-state placeholder.
/// Selection lives on the view model (one <see cref="Selection"/> for both
/// sides); the panel just renders rows reactively against the shared selection and
/// forwards clicks (with modifiers) to a callback that routes into the VM.
///
/// Row scroll/hit-test/wheel/double-click plumbing lives in <see cref="VirtualRowListView"/>.
/// This view owns the per-row drawing (status badge + path text) and the empty-state swap.
/// </summary>
internal sealed class LocalChangesPanel : ContainerView
{
    private readonly Context _ctx;
    private readonly ICanvas _canvas;
    private readonly ILocalizationService _loc;
    private readonly Func<Strings, string> _titleSelector;
    private readonly DiffSide _side;
    private readonly IReadable<Selection> _selection;
    private readonly Action<FileRow, InputModifiers> _onRowClick;
    private readonly Action<FileRow>? _onRowActivated;
    private readonly Action? _onEmptyAreaClicked;
    private readonly Action<FileRow>? _onFolderToggle;
    private readonly Func<FileRow?, IReadOnlyList<RepoBarContextMenu.Item>>? _buildContextMenu;
    private readonly TextView _headerText;
    private readonly View _emptyPlaceholder;
    private readonly PaddingView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBar _scrollBar;

    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();
    private IReadOnlyList<FileRow> _rows = Array.Empty<FileRow>();
    private FileViewMode _viewMode = FileViewMode.Flat;
    private IReadOnlySet<string> _collapsed = new HashSet<string>();

    private readonly SpinnerAnimation _pendingSpinner;
    // Paths with an index move in flight (both sides share the set); the spinner runs only while
    // one of them is actually a row on this side.
    private IReadOnlySet<string> _pending = new HashSet<string>();

    // A single floating selection bar that slides between rows — but only while exactly one row
    // is selected. Multi-select falls back to static per-row fills (_selectedIndex = -1 hides the
    // bar); the bar draws at CurrentSelectionIndex(), lerping _animFromIndex → _animToIndex.
    private readonly Tween _selectionTween;
    private int _selectedIndex = -1;
    private float _animFromIndex;
    private float _animToIndex;
    private View _currentBody = null!;
    private string? _lastChevronTogglePath;
    private int _lastChevronToggleTick;

    private readonly TextStyle _statusIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Default,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _pathTextStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };
    private readonly TextStyle _pathTextActiveStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };
    private readonly TextStyle _chevronStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _folderIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };

    private FileChangeRowStyles _rowStyles = ThemeStyles.Dark.FileChangeRow;
    private RowSelectionStyles _rowSelection = ThemeStyles.Dark.RowSelection;

    public IReadOnlyList<FileChange> Files => _files;

    public LocalChangesPanel(
        Context ctx,
        Func<Strings, string> title,
        DiffSide side,
        View emptyPlaceholder,
        IReadable<Selection> selection,
        Action<FileRow, InputModifiers> onRowClick,
        IReadOnlyList<View>? headerActions = null,
        Action<FileRow>? onRowActivated = null,
        Action? onEmptyAreaClicked = null,
        Action<FileRow>? onFolderToggle = null,
        Func<FileRow?, IReadOnlyList<RepoBarContextMenu.Item>>? buildContextMenu = null)
    {
        _ctx = ctx;
        _canvas = ctx.Canvas;
        _loc = ctx.Localization();
        _titleSelector = title;
        _side = side;
        _selection = selection;
        _onRowClick = onRowClick;
        _onRowActivated = onRowActivated;
        _onEmptyAreaClicked = onEmptyAreaClicked;
        _onFolderToggle = onFolderToggle;
        _buildContextMenu = buildContextMenu;
        var input = ctx.Require<InputSystem>();

        _headerText = FileChangesUI.CreateHeaderText(ctx, _titleSelector(_loc.Strings.Value));
        _emptyPlaceholder = emptyPlaceholder;

        // Turns the loader on this side's in-flight rows while any has a stage/unstage running in
        // git; parked (no ticking) otherwise.
        _pendingSpinner = new SpinnerAnimation(ctx.Require<IFrameTicker>());
        var headerContent = FileChangesUI.CreateHeaderContent(_headerText, headerActions);

        var headerBar = FileChangesUI.CreateHeaderBar(
            ctx, headerContent, topBorder: false, background: RepoContentTabs.Content);

        // Parks itself when settled so it adds no idle repaints. EaseOutCubic = quick start,
        // gentle landing.
        _selectionTween = new Tween(ctx.Require<IFrameTicker>(), 0.18f, Easings.EaseOutCubic);

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
            SelectionOverlayBuilder = DrawSelectionOverlay,
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.RowClicked += OnRowClicked;
        if (onRowActivated != null) _list.RowActivated += OnRowActivated;
        if (buildContextMenu != null) _list.RowContextRequested += OnRowContextRequested;

        // Empty placeholder swaps in as the body when there are no files; the widget swaps
        // back in when files arrive. Keeps the layout (header / center / scrollbars) intact.
        _bodyContainer = new PaddingView
        {
            Padding = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
        };
        _bodyContainer.Children.Add(_emptyPlaceholder);
        _currentBody = _emptyPlaceholder;

        _scrollBar = ScrollBars.CreateVertical(ctx);

        AddChildToSelf(new BorderLayoutView
        {
            North = headerBar,
            Center = _bodyContainer,
            East = _scrollBar,
        });

        _list.UseController(input, () => new VirtualRowListController(_list));

        // Retarget the floating bar on selection change (slides only for single-select; multi
        // snaps the bar away), then repaint each tick while it slides.
        this.Bind(selection, sel => { UpdateSelectionAnimation(sel); SetDirty(); });
        this.Bind(_selectionTween.Progress, _ => SetDirty());
        this.Use(() => _selectionTween);
        this.Bind(_pendingSpinner.Rotation, _ => SetDirty());
        this.Use(() => _pendingSpinner);

        this.BindThemed(ctx.Theme(), s =>
        {
            _rowStyles = s.FileChangeRow;
            _rowSelection = s.RowSelection;
            _pathTextStyle.TextColor = _rowStyles.RowText;
            _pathTextActiveStyle.TextColor = _rowSelection.Text;
            _chevronStyle.TextColor = _rowStyles.RowText;
            _folderIconStyle.TextColor = _rowStyles.RowText;
            SetDirty();
        });

        this.Use(() => new ScrollSyncController(_list, _scrollBar));

        // The header reads "Title (count)"; the title is localized, so re-render it on a live
        // locale switch (the count is preserved from the current file list).
        this.Bind(_loc.Strings, _ => UpdateHeaderText());
    }

    private void UpdateHeaderText() =>
        _headerText.Text = FileChangesUI.FormatHeader(_titleSelector(_loc.Strings.Value), _files.Count);

    public void SetFiles(IReadOnlyList<FileChange> files)
    {
        _files = files;
        UpdateHeaderText();
        RebuildRows();
        SyncPendingSpinner();
        // New data: jump back to the top rather than preserving a now-meaningless offset.
        _list.SetScrollY(0f);
    }

    public void SetPending(IReadOnlySet<string> pending)
    {
        _pending = pending;
        SyncPendingSpinner();
        SetDirty();
    }

    private void SyncPendingSpinner()
    {
        if (HasPendingRow()) _pendingSpinner.Start();
        else _pendingSpinner.Stop();
    }

    private bool HasPendingRow()
    {
        if (_pending.Count == 0) return false;
        foreach (var f in _files)
            if (_pending.Contains(f.Path)) return true;
        return false;
    }

    // Scrolls just enough to bring the keyboard cursor's row into view. Ignores cursors on
    // the other side (each panel renders only its own side's rows).
    public void EnsureRowVisible(FileRowRef row)
    {
        if (row.Side != _side) return;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.FullPath == row.FullPath && (r.Kind == FileRowKind.Folder) == row.IsFolder)
            {
                _list.EnsureRowVisible(i);
                return;
            }
        }
    }

    public void SetViewMode(FileViewMode mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        RebuildRows();
    }

    public void SetCollapsed(IReadOnlySet<string> collapsed)
    {
        _collapsed = collapsed;
        RebuildRows();
    }

    private void RebuildRows()
    {
        _rows = FileTreeBuilder.BuildRows(_files, _side, _viewMode, _collapsed);
        // Only swap the body on a real empty↔non-empty transition. Detaching and
        // re-adding the list view (e.g. on every folder collapse) churns its
        // InputSystem controller registration and drops the hover path, so clicks
        // stop landing until the cursor physically re-enters. Keeping it mounted —
        // as BranchesView does — avoids that.
        SetBody(_rows.Count == 0 ? _emptyPlaceholder : _list);
        _list.ItemCount = _rows.Count;
        _list.NotifyItemsChanged();

        // Rows shifted under the selection (collapse, reload): re-resolve and park the bar there
        // without sliding — the contents moved, not the user.
        SnapSelectionToCurrent();
    }

    private void SetBody(View body)
    {
        if (ReferenceEquals(_currentBody, body)) return;
        _bodyContainer.Children.Clear();
        _bodyContainer.Children.Add(body);
        _currentBody = body;
    }

    private void OnRowClicked(int rowIndex, InputModifiers modifiers, PointF point)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            _onEmptyAreaClicked?.Invoke();
            return;
        }
        var row = _rows[rowIndex];
        // The chevron toggles the folder without disturbing the selection — it consumes
        // the click before the row's select handler runs.
        if (row.Kind == FileRowKind.Folder && _onFolderToggle != null && IsChevronHit(row, point))
        {
            // RowClicked fires on every physical click, so a double-click would toggle
            // twice (a net no-op). Swallow the second click of a double on the same
            // chevron so a double-click is a single, predictable toggle.
            var now = Environment.TickCount;
            if (_lastChevronTogglePath == row.FullPath
                && unchecked(now - _lastChevronToggleTick) <= _list.DoubleClickThresholdMs)
            {
                _lastChevronTogglePath = null;
                return;
            }
            _lastChevronTogglePath = row.FullPath;
            _lastChevronToggleTick = now;
            _onFolderToggle(row);
            return;
        }
        _onRowClick(row, modifiers);
    }

    // The chevron occupies the indent + chevron column at the left of a folder row; a hit
    // anywhere from the row's left edge through the chevron toggles (so the small triangle
    // isn't a pixel-perfect target), while the icon and name select.
    private bool IsChevronHit(FileRow row, PointF point)
    {
        var chevronRight = _list.Position.Left + FileChangesUI.RowPaddingLeft
            + row.Indent + FileChangesUI.ChevronWidth + FileChangesUI.ChevronGap;
        // The chevron column mirrors to the right edge under RTL, so the hit region flips with it.
        return IsRtl
            ? point.X >= _list.Position.Left + _list.Position.Right - chevronRight
            : point.X <= chevronRight;
    }

    private void OnRowActivated(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        _onRowActivated?.Invoke(_rows[rowIndex]);
    }

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        if (_buildContextMenu == null) return;

        var onRow = rowIndex >= 0 && rowIndex < _rows.Count;
        var target = onRow ? _rows[rowIndex] : null;

        var items = _buildContextMenu(target);
        if (items.Count == 0) return;

        if (onRow) _list.SetContextHighlight(rowIndex);
        var opened = RepoBarContextMenu.Show(_ctx, point, items);
        if (opened == null)
        {
            _list.SetContextHighlight(null);
            return;
        }
        opened.Closed += () => _list.SetContextHighlight(null);
    }

    private void DrawFileRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var row = _rows[rowIndex];
        var selection = _selection.Value;
        var isSelected = selection.ContainsRow(row.Ref);

        // The floating bar (single-select only) owns its target row's fill; suppress the static
        // one there so they don't double-paint. -1 (multi/none) leaves every row drawing statically.
        var floatsBar = _selectedIndex >= 0 && rowIndex == _selectedIndex;

        if (row.Kind == FileRowKind.Folder)
        {
            FileChangesUI.DrawFolderRow(
                _canvas,
                rowRect,
                row.DisplayName,
                row.Indent,
                row.IsOpen,
                isSelected,
                state.IsHovered || state.IsContextHighlighted,
                _rowSelection,
                _chevronStyle,
                _folderIconStyle,
                _pathTextStyle,
                _pathTextActiveStyle,
                z,
                isRtl: IsRtl,
                drawSelectionBackground: !floatsBar,
                guides: row.Guides);
            return;
        }

        var file = row.File!;
        FileChangesUI.DrawFileRow(
            _canvas,
            rowRect,
            file,
            isSelected,
            state.IsHovered || state.IsContextHighlighted,
            _rowSelection,
            _rowStyles,
            _pathTextStyle,
            _pathTextActiveStyle,
            _statusIconStyle,
            z,
            row.DisplayName,
            row.Indent,
            reserveChevronColumn: _viewMode == FileViewMode.Tree,
            isRtl: IsRtl,
            drawSelectionBackground: !floatsBar,
            guides: row.Guides,
            isPending: _pending.Count > 0 && _pending.Contains(file.Path),
            pendingRotation: _pendingSpinner.Rotation.Value);
    }

    // Retargets the floating bar from the current selection: the lone selected row's index when
    // exactly one is selected, else -1 (multi/none → no bar).
    private void UpdateSelectionAnimation(Selection sel)
        => MoveSelectionTo(sel.Count == 1 ? IndexOfRow(sel.Rows[0]) : -1);

    // Slides only between two real rows (single → single); first-select, clear, and any
    // transition through multi-select snap in place (sliding in from nowhere reads as a glitch).
    private void MoveSelectionTo(int newIndex)
    {
        if (newIndex == _selectedIndex) return;
        if (_selectedIndex >= 0 && newIndex >= 0)
        {
            _animFromIndex = CurrentSelectionIndex();
            _animToIndex = newIndex;
            _selectedIndex = newIndex;
            _selectionTween.Restart();
        }
        else
        {
            _selectedIndex = newIndex;
            _animFromIndex = newIndex < 0 ? 0f : newIndex;
            _animToIndex = _animFromIndex;
        }
    }

    // Re-resolves the bar's row from the current selection and parks it there without animating.
    private void SnapSelectionToCurrent()
    {
        var sel = _selection.Value;
        _selectedIndex = sel.Count == 1 ? IndexOfRow(sel.Rows[0]) : -1;
        _animFromIndex = _selectedIndex < 0 ? 0f : _selectedIndex;
        _animToIndex = _animFromIndex;
    }

    private int IndexOfRow(FileRowRef target)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Ref.Equals(target)) return i;
        return -1;
    }

    private float CurrentSelectionIndex()
        => _animFromIndex + (_animToIndex - _animFromIndex) * _selectionTween.Progress.Value;

    // One selection bar for the whole list, floated below row content by VirtualRowListView so it
    // rides scroll and slides between rows. Shares the RowSelection painter so its look matches the
    // static fills multi-select draws.
    private void DrawSelectionOverlay(ICanvas c, RectF viewport, int z)
    {
        if (_selectedIndex < 0) return;
        var index = CurrentSelectionIndex();
        var rowTop = viewport.Top + _list.ScrollY - index * FileChangesUI.RowHeight;
        var rowRect = new RectF(viewport.Left, rowTop - FileChangesUI.RowHeight, viewport.Width, FileChangesUI.RowHeight);
        RowSelection.DrawBackground(c, rowRect, isSelected: true, isHovered: false, _rowSelection, z, isRtl: IsRtl);
    }
}
