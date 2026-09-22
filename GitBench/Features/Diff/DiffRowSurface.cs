using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

/// <summary>
/// One stream of diff rows drawn through a <see cref="VirtualRowListView"/>: where each row's
/// text starts, what is under a pointer (a character, a gap expander, a fold, a usages lens, a
/// hunk button), the hunk hover chrome, and the per-row paint. The single-file pane owns one over
/// the whole list; the review window owns one per file card, each offset to where its rows sit in
/// the shared list and inset to the card's edges.
/// </summary>
internal sealed class DiffRowSurface
{
    private const float FallbackMonoAdvanceRatio = 0.6f;
    private const float GutterPadding = 8f;
    private const float HunkOutlineThickness = 1f;

    private readonly VirtualRowListView _list;
    private readonly DiffRowPainter _painter;
    private readonly HunkButtonBar _buttonBar;
    private readonly DiffListScroll _scroll;
    private readonly Action _redraw;

    private int _hoveredHunk = -1;
    private HunkAction _hoveredButton = HunkAction.None;
    private int _hoveredExpanderRow = -1;
    private int _hoveredFoldRow = -1;
    private int _hoveredLensRow = -1;

    public DiffRowSurface(
        VirtualRowListView list, DiffRowPainter painter, HunkButtonBar buttonBar, DiffListScroll scroll,
        Action redraw)
    {
        _list = list;
        _painter = painter;
        _buttonBar = buttonBar;
        _scroll = scroll;
        _redraw = redraw;
    }

    public required DiffSelectionModel Selection { get; init; }

    /// <summary>The selection scope these rows answer to; the review list scopes each card by path.</summary>
    public object? Scope { get; init; }

    /// <summary>How far the rows sit in from the list's left and right edges.</summary>
    public float InsetX { get; init; }

    public IDiffRowSource Rows { get; set; } = DiffRowSet.Empty;

    /// <summary>The list row that row 0 draws on.</summary>
    public int FirstRow { get; set; }

    public DiffHunkButtonStyles ButtonStyles { get; set; } = ThemeStyles.Dark.DiffHunkButton;

    /// <summary>Whether hunks grow the outline and Stage/Unstage/Discard pills on hover.</summary>
    public bool HunkButtons { get; set; }

    public DiffSide Side { get; set; }

    public IReadOnlyList<WorkingTreeHunkState>? HunkStates { get; set; }

    public UsageLensOverlay UsageLens { get; set; } = UsageLensOverlay.Empty;

    public Action<int>? StageHunk { get; set; }
    public Action<int>? UnstageHunk { get; set; }
    public Action<int>? DiscardHunk { get; set; }
    public Action<int, GapExpandDirection>? ExpandGap { get; set; }
    public Action<int, GapExpandDirection>? ExpandGapToDeclaration { get; set; }
    public Action<string>? ToggleFold { get; set; }
    public Action<UsageLensTarget, PointF>? ActivateLens { get; set; }

    public static void ResolveMetrics(DiffRowPainter painter, ICanvas c)
    {
        if (painter.LineHeight > 0) return;
        painter.LineHeight = c.MeasureTextLineHeight(painter.MonoMetricsStyle);
        var measured = c.MeasureTextWidth("0", painter.MonoMetricsStyle);
        painter.MonoAdvance = measured > 0 ? measured : painter.CodeFontSize * FallbackMonoAdvanceRatio;
    }

    public float LineHeight => _painter.LineHeight;

    public float MonoAdvance => _painter.MonoAdvance;

    private float Advance => MonoAdvance > 0 ? MonoAdvance : _painter.CodeFontSize * FallbackMonoAdvanceRatio;

    public float GutterWidth => Rows.GutterDigits * Advance + GutterPadding;

    public RectF Frame
    {
        get
        {
            var p = _list.Position;
            return new RectF(p.Left + InsetX, p.Bottom, Math.Max(0f, p.Width - InsetX * 2), p.Height);
        }
    }

    public float ContentLeft => Frame.Left - _scroll.X;

    public float NaturalWidth()
    {
        var gutters = Rows.SingleGutter ? GutterWidth : GutterWidth * 2;
        return DiffRowPainter.MarkerLaneWidth
            + gutters + DiffRowPainter.FoldColumnWidthOf(Rows.FoldColumn)
            + DiffRowPainter.GlyphColumnWidthOf(Rows.GlyphColumn)
            + Rows.MaxRowCells * Advance + DiffRowPainter.BannerPaddingX;
    }

    public float TextOriginX() => DiffRowPainter.LineTextOriginX(
        ContentLeft, GutterWidth, Rows.SingleGutter, Rows.FoldColumn, Rows.GlyphColumn);

    /// <summary>The monospace cell an x falls in, unclamped — negative over the gutters, past the
    /// line's own cell count out in the margin.</summary>
    public float CellAt(float x) => MonoAdvance <= 0 ? 0f : (x - TextOriginX()) / MonoAdvance;

    public ExpandedColumn CharIndexAt(string text, float x) =>
        MonoAdvance <= 0 ? default : new ExpandedColumn(DiffText.CharIndexAtCell(text, CellAt(x)));

    public bool TryGetRowRect(int row, out RectF rect)
    {
        if (row >= 0 && row < Rows.Rows.Count) return _list.TryGetRowRect(FirstRow + row, out rect);
        rect = default;
        return false;
    }

    public int RowAt(PointF point)
    {
        if (LineHeight <= 0) return -1;
        var row = _list.RowIndexAt(point) - FirstRow;
        return row >= 0 && row < Rows.Rows.Count ? row : -1;
    }

    public DiffTextPos? TextPosAt(PointF point)
    {
        var row = RowAt(point);
        if (row < 0 || Rows.Rows[row] is not DiffRow.Line line) return null;
        return new DiffTextPos(new RowIndex(row), CharIndexAt(line.Text.Expanded, point.X));
    }

    public int HunkIndexOf(int row) => Rows.Hunks?.HunkIndexOf(row) ?? -1;

    private HunkRowRange? RangeOf(int hunkIndex) =>
        Rows.Hunks is { Ranges: var ranges } && hunkIndex >= 0 && hunkIndex < ranges.Count ? ranges[hunkIndex] : null;

    private HunkAction[] ActionsForHunk(int hunkIndex) => HunkButtonBar.ActionsFor(HunkStates, hunkIndex, Side);

    public (int Hunk, HunkAction Action)? HunkButtonAt(PointF point)
    {
        if (!HunkButtons) return null;
        var hunk = HunkIndexOf(RowAt(point));
        if (hunk < 0) return null;
        var action = ButtonAt(point, hunk);
        return action == HunkAction.None ? null : (hunk, action);
    }

    private HunkAction ButtonAt(PointF point, int hunkIndex)
    {
        if (RangeOf(hunkIndex) is not { } range) return HunkAction.None;
        if (!TryGetRowRect(HunkButtonBar.ButtonRowFor(range), out var rowRect)) return HunkAction.None;
        return _buttonBar.HitTest(point, Frame.Right, rowRect.Top, ActionsForHunk(hunkIndex));
    }

    public (int Row, int GapIndex, GapExpandDirection Dir)? ExpanderAt(PointF point)
    {
        var row = RowAt(point);
        if (row < 0 || DiffRowPainter.GapBarOf(Rows.Rows[row]) is not { } gap) return null;
        if (DiffRowPainter.ExpanderHit(gap, point.X - ContentLeft) is not { } dir) return null;
        return (row, gap.GapIndex, dir);
    }

    // Two targets for one fold: the chevron in the margin, and the pill standing in for the body
    // it swallowed — which is the one a reader reaches for, because it is the thing they can see.
    public (int Row, string Id)? FoldAt(PointF point)
    {
        if (!Rows.FoldColumn) return null;
        var row = RowAt(point);
        if (row < 0 || Rows.Rows[row] is not DiffRow.Line { Fold: { } fold } line) return null;

        if (fold.Chevron && DiffRowPainter.FoldHit(point.X - ContentLeft, GutterWidth, Rows.SingleGutter))
            return (row, fold.Id);

        if (!fold.Chip) return null;
        var (chipX, chipWidth) = _painter.FoldChipBounds(line, TextOriginX());
        return point.X >= chipX && point.X <= chipX + chipWidth ? (row, fold.Id) : null;
    }

    // Only the words are the target, not the whole row: a lens row spans the file's full width and
    // clicking the empty margin beside one should still start a selection.
    public (int Row, UsageLensTarget Target, PointF Anchor)? LensAt(PointF point)
    {
        if (UsageLens.IsEmpty) return null;
        var row = RowAt(point);
        if (row < 0 || Rows.Rows[row] is not DiffRow.Lens lens) return null;
        if (UsageLens.On(lens.At) is not { } state) return null;

        var (x, width) = _painter.LensBounds(lens, _painter.LensLabel(state), TextOriginX());
        if (width <= 0f || point.X < x || point.X > x + width) return null;

        // Anchored to the words rather than to the pixel clicked, so what opens hangs off the lens
        // the way it does off a menu, wherever in it the reader happened to click.
        var anchor = TryGetRowRect(row, out var rect) ? new PointF(x, rect.Bottom) : point;
        return (row, TargetOf(lens), anchor);
    }

    public static UsageLensTarget TargetOf(DiffRow.Lens lens) => new(lens.Id, lens.At, lens.NameLine, lens.NameColumn);

    public bool IsInteractiveAt(PointF point) =>
        ExpanderAt(point) != null || FoldAt(point) != null || LensAt(point) != null || HunkButtonAt(point) != null;

    public MouseCursor CursorAt(PointF point)
    {
        if (IsInteractiveAt(point)) return MouseCursor.Hand;
        return TextPosAt(point) != null ? MouseCursor.Text : MouseCursor.Default;
    }

    public void PointerMoved(PointF point)
    {
        SetHover(ref _hoveredExpanderRow, ExpanderAt(point)?.Row ?? -1);
        SetHover(ref _hoveredFoldRow, FoldAt(point)?.Row ?? -1);
        SetHover(ref _hoveredLensRow, LensAt(point)?.Row ?? -1);

        var hunk = -1;
        var button = HunkAction.None;
        if (HunkButtons)
        {
            hunk = HunkIndexOf(RowAt(point));
            if (hunk >= 0) button = ButtonAt(point, hunk);
        }
        SetHunkHover(hunk, button);
    }

    public void ClearHover()
    {
        SetHover(ref _hoveredExpanderRow, -1);
        SetHover(ref _hoveredFoldRow, -1);
        SetHover(ref _hoveredLensRow, -1);
        SetHunkHover(-1, HunkAction.None);
    }

    private void SetHover(ref int field, int row)
    {
        if (field == row) return;
        field = row;
        _redraw();
    }

    private void SetHunkHover(int hunk, HunkAction button)
    {
        if (_hoveredHunk == hunk && _hoveredButton == button) return;
        _hoveredHunk = hunk;
        _hoveredButton = button;
        _redraw();
    }

    public bool Click(PointF point, InputModifiers modifiers) =>
        ClickLens(point) || ClickFold(point) || ClickHunkButton(point) || ClickExpander(point, modifiers);

    public bool ClickLens(PointF point)
    {
        if (LensAt(point) is not { } hit) return false;
        ActivateLens?.Invoke(hit.Target, hit.Anchor);
        return true;
    }

    public bool ClickFold(PointF point)
    {
        if (FoldAt(point) is not { } hit) return false;
        ToggleFold?.Invoke(hit.Id);
        return true;
    }

    public bool ClickHunkButton(PointF point)
    {
        if (HunkButtonAt(point) is not { } hit) return false;
        switch (hit.Action)
        {
            case HunkAction.Stage: StageHunk?.Invoke(hit.Hunk); break;
            case HunkAction.Unstage: UnstageHunk?.Invoke(hit.Hunk); break;
            case HunkAction.Discard: DiscardHunk?.Invoke(hit.Hunk); break;
        }
        return true;
    }

    public bool ClickExpander(PointF point, InputModifiers modifiers)
    {
        if (ExpanderAt(point) is not { } hit) return false;
        // Unfold-all already means "all of it", so the modifier only changes what a stepping
        // chevron counts as one step.
        var handler = modifiers.HasFlag(InputModifiers.Alt) && hit.Dir != GapExpandDirection.All
            ? ExpandGapToDeclaration ?? ExpandGap
            : ExpandGap;
        handler?.Invoke(hit.GapIndex, hit.Dir);
        return true;
    }

    public DiffRowPaint PaintFor(RectF rowRect, int row, int z)
    {
        var drawn = Rows.Rows[row];
        DiffRowSelection? selection = null;
        if (drawn is DiffRow.Line line
            && Selection.TryRowSpan(Scope, new RowIndex(row), line.Text.End, out var span))
            selection = span;

        return new DiffRowPaint(
            ContentLeft, rowRect.Bottom, _scroll.ContentWidth, GutterWidth, Rows.SingleGutter,
            ExpanderHovered: row == _hoveredExpanderRow,
            Viewport: _list.Position,
            Z: z,
            Selection: selection,
            FoldColumn: Rows.FoldColumn,
            FoldHovered: row == _hoveredFoldRow,
            GlyphColumn: Rows.GlyphColumn,
            Usages: drawn is DiffRow.Lens lens ? UsageLens.On(lens.At) : null,
            LensHovered: row == _hoveredLensRow);
    }

    public void DrawRow(ICanvas c, RectF rowRect, int row, int z) =>
        DrawRow(c, rowRect, row, z, Rows.Rows[row], PaintFor(rowRect, row, z));

    public void DrawRow(ICanvas c, RectF rowRect, int row, int z, DiffRow drawn, in DiffRowPaint paint)
    {
        _painter.DrawRow(c, drawn, paint);

        var hunk = HunkIndexOf(row);
        if (hunk < 0 || hunk != _hoveredHunk || !HunkButtons || RangeOf(hunk) is not { } range) return;
        DrawHunkOutline(c, rowRect, row, range, z + 5);
        if (row == HunkButtonBar.ButtonRowFor(range))
            _buttonBar.Draw(c, Frame.Right, rowRect.Top, ActionsForHunk(hunk), _hoveredButton, ButtonStyles, z + 7);
    }

    // 1px sides on every row of the hunk, closed by a top edge on its first row and a bottom edge
    // on its last.
    private void DrawHunkOutline(ICanvas c, RectF rowRect, int row, HunkRowRange range, int z)
    {
        var frame = Frame;
        var style = new RectStyle { BackgroundColor = _painter.Styles.HunkOutline };
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(frame.Left, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = style,
            ZIndex = z,
        });
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(frame.Right - HunkOutlineThickness, rowRect.Bottom, HunkOutlineThickness, rowRect.Height),
            Style = style,
            ZIndex = z,
        });
        if (row == range.FirstRow)
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(frame.Left, rowRect.Top - HunkOutlineThickness, frame.Width, HunkOutlineThickness),
                Style = style,
                ZIndex = z,
            });
        if (row == range.LastRow)
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(frame.Left, rowRect.Bottom, frame.Width, HunkOutlineThickness),
                Style = style,
                ZIndex = z,
            });
    }
}
