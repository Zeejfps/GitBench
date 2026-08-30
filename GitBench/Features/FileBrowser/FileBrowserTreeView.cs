using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The browser's tree rail: a virtualized row list over
/// <see cref="FileBrowserViewModel.Rows"/>, with arrow-key navigation and a scroll bar.
/// </summary>
/// <remarks>
/// Virtualized rather than a widget per row: a single <c>node_modules</c> is forty thousand entries,
/// and a view subtree per entry is not a thing that can be scrolled. Nothing here holds a copy of
/// the model — the row list is the view model's, published whole, and the cursor is read back from
/// it — so a re-list can never leave the painter and the keyboard disagreeing about what row 12 is.
/// </remarks>
internal sealed class FileBrowserTreeView : ContainerView, IScrollableContent
{
    private readonly FileBrowserViewModel _vm;
    private readonly ICanvas _canvas;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly ListArrowKbmController _arrows;

    private IReadOnlyList<FileBrowserRow> _rows = [];
    private RowSelectionStyles _selection = ThemeStyles.Dark.RowSelection;
    private FileBrowserRowStyles _rowColors = ThemeStyles.Dark.FileBrowserRow;

    private float _lastVerticalScale = -1f;
    private float _lastNormalizedY;
    private bool _publishedHorizontal;

    private readonly TextStyle _chevronStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _iconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _textStyle = new()
    {
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _textActiveStyle = new()
    {
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };

    public event Action<float>? VerticalScrollPositionChanged;

    public event Action<float>? HorizontalScrollPositionChanged;

    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale => 1f;

    /// <summary>Where a right-click landed, for the pane's context menu. Null when it landed on no
    /// row.</summary>
    public event Action<FileBrowserRow?, PointF>? RowContextRequested;

    public FileBrowserTreeView(Context ctx, FileBrowserViewModel vm)
    {
        _vm = vm;
        _canvas = ctx.Canvas;
        var input = ctx.Require<InputSystem>();

        _list = new VirtualRowListView
        {
            RowHeight = FileBrowserRowPainter.RowHeight,
            ItemBuilder = DrawRowAt,
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.RowClicked += OnRowClicked;
        _list.RowActivated += OnRowActivated;
        _list.RowContextRequested += OnRowContextRequested;
        _list.ScrollChanged += NotifyScrollChanged;

        _scrollBar = ScrollBars.CreateVertical(ctx);

        AddChildToSelf(new BorderLayoutView
        {
            Center = new PaddingView
            {
                Padding = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
                Children = { _list },
            },
            East = _scrollBar,
        });

        _list.UseController(input, () => new VirtualRowListController(_list));

        _arrows = new ListArrowKbmController(
            this,
            input,
            onMove: (delta, _) => _vm.MoveCursor(delta),
            onExpand: open => { if (open) _vm.ExpandOrDescend(); else _vm.CollapseOrAscend(); },
            onActivate: ActivateCursor,
            onDelete: () => { });
        this.UseController(input, _arrows);

        this.Bind(vm.Rows, SetRows);
        this.Bind(vm.Cursor, _ => { EnsureCursorVisible(); SetDirty(); });

        this.BindThemed(ctx.Theme(), s =>
        {
            _selection = s.RowSelection;
            _rowColors = s.FileBrowserRow;
            SetDirty();
        });

        this.Use(() => new ScrollSyncController(this, _scrollBar, null));
    }

    private void SetRows(IReadOnlyList<FileBrowserRow> rows)
    {
        _rows = rows;
        _list.ItemCount = rows.Count;
        _list.NotifyItemsChanged();
        EnsureCursorVisible();
        NotifyScrollChanged();
        SetDirty();
    }

    private void EnsureCursorVisible()
    {
        var index = _vm.IndexOfCursor(_rows);
        if (index >= 0) _list.EnsureRowVisible(index);
    }

    private void ActivateCursor()
    {
        var index = _vm.IndexOfCursor(_rows);
        if (index >= 0) _vm.Activate(_rows[index]);
    }

    private void OnRowClicked(int rowIndex, InputModifiers modifiers, PointF point)
    {
        _arrows.TakeFocus();
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var row = _rows[rowIndex];
        if (row is FileBrowserRow.Directory directory && IsChevronHit(row, point))
        {
            _vm.Toggle(directory);
            return;
        }

        _vm.SetCursor(row.FullPath);
    }

    private bool IsChevronHit(FileBrowserRow row, PointF point)
    {
        var chevronRight = _list.Position.Left + FileBrowserRowPainter.RowPaddingLeft
            + row.Depth * FileBrowserRowPainter.IndentLevel
            + FileBrowserRowPainter.ChevronWidth + FileBrowserRowPainter.ChevronGap;
        return IsRtl
            ? point.X >= _list.Position.Left + _list.Position.Right - chevronRight
            : point.X <= chevronRight;
    }

    private void OnRowActivated(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        _vm.Activate(_rows[rowIndex]);
    }

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        var onRow = rowIndex >= 0 && rowIndex < _rows.Count;
        if (onRow)
        {
            _list.SetContextHighlight(rowIndex);
            _vm.SetCursor(_rows[rowIndex].FullPath);
        }
        RowContextRequested?.Invoke(onRow ? _rows[rowIndex] : null, point);
    }

    /// <summary>Drops the right-click highlight once the menu it belongs to has closed.</summary>
    public void ClearContextHighlight() => _list.SetContextHighlight(null);

    private void DrawRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var row = _rows[rowIndex];
        FileBrowserRowPainter.Draw(
            _canvas,
            rowRect,
            row,
            isSelected: rowIndex == _vm.IndexOfCursor(_rows),
            isHovered: state.IsHovered || state.IsContextHighlighted,
            _selection,
            _rowColors,
            _chevronStyle,
            _iconStyle,
            _textStyle,
            _textActiveStyle,
            z,
            IsRtl);
    }

    protected override void OnDrawSelf(ICanvas c) => NotifyScrollChanged();


    public void SetVerticalNormalizedScrollPosition(float normalized)
    {
        var range = _rows.Count * FileBrowserRowPainter.RowHeight - _list.Position.Height;
        _list.SetScrollY(range <= 0 ? 0f : Math.Clamp(normalized, 0f, 1f) * range);
    }

    public void SetHorizontalNormalizedScrollPosition(float normalized) { /* no-op */ }

    private void NotifyScrollChanged()
    {
        var contentHeight = _rows.Count * FileBrowserRowPainter.RowHeight;
        var bodyHeight = _list.Position.Height;

        float scale, normalizedY;
        if (contentHeight <= bodyHeight || bodyHeight <= 0)
        {
            scale = 1f;
            normalizedY = 0f;
        }
        else
        {
            scale = bodyHeight / contentHeight;
            normalizedY = Math.Clamp(_list.ScrollY / (contentHeight - bodyHeight), 0f, 1f);
        }

        VerticalScale = scale;

        if (Math.Abs(scale - _lastVerticalScale) > 0.0001f
            || Math.Abs(normalizedY - _lastNormalizedY) > 0.0001f)
        {
            _lastVerticalScale = scale;
            _lastNormalizedY = normalizedY;
            VerticalScrollPositionChanged?.Invoke(normalizedY);
        }

        if (_publishedHorizontal) return;
        _publishedHorizontal = true;
        HorizontalScrollPositionChanged?.Invoke(0f);
    }
}
