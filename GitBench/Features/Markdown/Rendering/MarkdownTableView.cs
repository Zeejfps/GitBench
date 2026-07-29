using GitBench.Features.Markdown.Parsing;
using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Draws one GFM table: header row, data rows, per-column auto widths from
/// <see cref="TableLayout"/>, wrapped cells via <see cref="RichTextLayout"/>. Mirrors
/// <see cref="RichTextView"/>'s shape — measures and draws through the <see cref="ICanvas"/> it
/// was built with, and recomputes layout only when the width or the cell lists change (the
/// <c>_wrappedForWidth</c> pattern), because measure and draw run every frame.
/// <para>
/// Geometry, pinned by <c>MarkdownTableViewTests</c>: columns are solved by
/// <see cref="TableLayout.Measure"/> at the laid-out width (<c>Position.Width</c> when drawing,
/// the given available width when measuring; a width below the min-content total still lays out
/// at min widths — the table simply overflows, which is what makes the widget's
/// <c>HorizontalScrollArea</c> nesting work). Each column's content is inset
/// <see cref="CellPaddingX"/> from either side; each row is <see cref="CellPaddingY"/> above and
/// below its tallest wrapped cell (cells are top-aligned within the row). Rows stack from
/// <c>Position.Top</c> downward: header row, then the <see cref="HeaderRuleThickness"/> header
/// rule in <see cref="HeaderRuleColor"/>, then data rows with a
/// <see cref="RowSeparatorThickness"/> rule in <see cref="RowSeparatorColor"/> between
/// consecutive rows (never after the last; a header-only table still draws its header rule).
/// Both rules span the table's drawn width. Per-column alignment offsets each wrapped line
/// within the column's content box: <c>Left</c>/<c>None</c> flush left, <c>Right</c> flush
/// right, <c>Center</c> centered.
/// </para>
/// <para>
/// <see cref="View.MeasureWidth"/> reports the min-content table width (Σ column min-content +
/// padding) — the narrowest width the table can lay out at without splitting unbreakable chunks.
/// Inside a <c>HorizontalScrollView</c> (which sizes content to
/// <c>max(viewport, MeasureWidth())</c>) this is exactly what makes the table fill and wrap at
/// the viewport when it can, and scroll at min widths only when it must.
/// <see cref="View.MeasureHeight(float)"/> sums row heights plus rule thicknesses at the column
/// widths solved for that available width.
/// </para>
/// <para>
/// Cell runs arrive pre-styled (the widget builds header cells bold via
/// <see cref="InlineRunBuilder"/>); the view draws each segment in its run's own style, plus the
/// inline-code chip in <see cref="CodeChipBackground"/> behind code segments, like
/// <see cref="RichTextView"/>.
/// </para>
/// </summary>
internal sealed class MarkdownTableView : View
{
    /// <summary>Horizontal cell padding, each side of every column (<c>Spacing.Md</c>).</summary>
    public const float CellPaddingX = 8f;

    /// <summary>Vertical cell padding, above and below every row (<c>Spacing.Xs</c>).</summary>
    public const float CellPaddingY = 4f;

    /// <summary>The heavier rule under the header row.</summary>
    public const float HeaderRuleThickness = 2f;

    /// <summary>The hairline between consecutive data rows.</summary>
    public const float RowSeparatorThickness = 1f;

    private const float ChipCornerRadius = 3f;
    private const float UnderlineThickness = 1f;

    private readonly ICanvas _canvas;
    private readonly RectStyle _chipStyle = new() { BorderRadius = BorderRadiusStyle.All(ChipCornerRadius) };

    // Solved table cache, the _wrappedForWidth pattern: valid while the width is (nearly)
    // unchanged and the three cell inputs are the same list instances.
    private Solved? _solved;
    private float _solvedForWidth;
    private IReadOnlyList<IReadOnlyList<RichTextRun>>? _solvedForHeader;
    private IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>>? _solvedForRows;
    private IReadOnlyList<ColumnAlignment>? _solvedForAlignments;

    // Min-content table width cache for MeasureWidthIntrinsic — width-independent, so it lives
    // separately and alternating measure-width / measure-height calls don't evict each other.
    private float _minTableWidth;
    private IReadOnlyList<IReadOnlyList<RichTextRun>>? _minForHeader;
    private IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>>? _minForRows;
    private IReadOnlyList<ColumnAlignment>? _minForAlignments;

    private IReadOnlyList<IReadOnlyList<RichTextRun>> _header =
        Array.Empty<IReadOnlyList<RichTextRun>>();
    private IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> _rows =
        Array.Empty<IReadOnlyList<IReadOnlyList<RichTextRun>>>();
    private IReadOnlyList<ColumnAlignment> _alignments = Array.Empty<ColumnAlignment>();

    public MarkdownTableView(ICanvas canvas)
    {
        _canvas = canvas;
        Accessibility = new AccessibilityInfo(AccessibilityRole.Text);
    }

    /// <summary>The header row's cells, one styled-run list per column. Assigning a different
    /// list instance invalidates the cached layout.</summary>
    public IReadOnlyList<IReadOnlyList<RichTextRun>> Header
    {
        get => _header;
        set => SetField(ref _header, value);
    }

    /// <summary>The data rows, one cell list per row, rectangular with the header (the parser
    /// guarantees it). Assigning a different list instance invalidates the cached layout.</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> Rows
    {
        get => _rows;
        set => SetField(ref _rows, value);
    }

    /// <summary>Per-column alignment from the delimiter row; its count is the column count.</summary>
    public IReadOnlyList<ColumnAlignment> Alignments
    {
        get => _alignments;
        set => SetField(ref _alignments, value);
    }

    /// <summary>Color of the heavier rule under the header row. From the theme
    /// (<c>MarkdownStyles.TableHeaderRule</c>) via <see cref="MarkdownTable"/>.</summary>
    public uint HeaderRuleColor { get; set; }

    /// <summary>Color of the hairline between data rows. From the theme
    /// (<c>MarkdownStyles.TableRowSeparator</c>) via <see cref="MarkdownTable"/>.</summary>
    public uint RowSeparatorColor { get; set; }

    /// <summary>Background of the inline-code chip behind code segments in cells. From the theme;
    /// 0 draws no chip.</summary>
    public uint CodeChipBackground { get; set; }

    // Always the min-content table width, even under an externally set Width — the layout system
    // resolves a set Width before consulting the intrinsic measure, and the scroll-view contract
    // (fill-and-wrap vs. scroll-at-min) depends on the intrinsic staying min-content.
    protected override float MeasureWidthIntrinsic() => MinTableWidth();

    protected override float MeasureHeightIntrinsic(float availableWidth)
    {
        return SolveFor(availableWidth).Height;
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var solved = SolveFor(Position.Width);
        if (solved.Columns.Widths.Count == 0)
            return;

        var z = GetDrawZIndex();
        var left = Position.Left;
        var tableWidth = solved.Columns.TableWidth;
        var y = Position.Top;

        // Header row, then its rule — a header-only table still draws the rule.
        DrawRow(c, solved, solved.HeaderCells, y, z);
        y -= 2f * CellPaddingY + solved.HeaderContentHeight;
        DrawRule(c, left, y, tableWidth, HeaderRuleThickness, HeaderRuleColor, z + 1);
        y -= HeaderRuleThickness;

        // Data rows, a separator between consecutive rows only (never after the last).
        for (var i = 0; i < solved.RowCells.Length; i++)
        {
            if (i > 0)
            {
                DrawRule(c, left, y, tableWidth, RowSeparatorThickness, RowSeparatorColor, z + 1);
                y -= RowSeparatorThickness;
            }
            DrawRow(c, solved, solved.RowCells[i], y, z);
            y -= 2f * CellPaddingY + solved.RowContentHeights[i];
        }
    }

    /// <summary>One solved layout: the columns, every cell's wrapped layout at its column width
    /// (with the segment slice strings pre-built so draws allocate nothing), the per-row content
    /// heights (tallest cell), and the table's total height.</summary>
    private sealed record Solved(
        TableColumns Columns,
        CellLayout[] HeaderCells,
        CellLayout[][] RowCells,
        float HeaderContentHeight,
        float[] RowContentHeights,
        float Height);

    /// <summary>One laid-out cell: its runs, the wrapped layout, and per-segment slice strings in
    /// line order (the same flattening <see cref="RichTextView"/> uses).</summary>
    private readonly record struct CellLayout(
        IReadOnlyList<RichTextRun> Runs,
        RichTextLayoutResult Layout,
        string[] SegmentTexts);

    private Solved SolveFor(float availableWidth)
    {
        if (_solved == null
            || !ReferenceEquals(_solvedForHeader, _header)
            || !ReferenceEquals(_solvedForRows, _rows)
            || !ReferenceEquals(_solvedForAlignments, _alignments)
            || Math.Abs(availableWidth - _solvedForWidth) >= 0.5f)
        {
            _solved = Solve(availableWidth);
            _solvedForWidth = availableWidth;
            _solvedForHeader = _header;
            _solvedForRows = _rows;
            _solvedForAlignments = _alignments;
        }
        return _solved;
    }

    private Solved Solve(float availableWidth)
    {
        var columns = TableLayout.Measure(
            _canvas, _header, _rows, _alignments, availableWidth, CellPaddingX);

        var headerCells = LayoutRow(_header, columns.Widths);
        var headerHeight = ContentHeight(headerCells);
        var hasColumns = columns.Widths.Count > 0;
        var height = hasColumns ? 2f * CellPaddingY + headerHeight + HeaderRuleThickness : 0f;

        var rowCells = new CellLayout[_rows.Count][];
        var rowHeights = new float[_rows.Count];
        for (var i = 0; i < _rows.Count; i++)
        {
            rowCells[i] = LayoutRow(_rows[i], columns.Widths);
            rowHeights[i] = ContentHeight(rowCells[i]);
            if (hasColumns)
                height += 2f * CellPaddingY + rowHeights[i] + (i > 0 ? RowSeparatorThickness : 0f);
        }

        return new Solved(columns, headerCells, rowCells, headerHeight, rowHeights, height);
    }

    /// <summary>Wraps one row's cells at the solved column widths. A missing cell (defensive —
    /// the parser guarantees rectangular rows) lays out as empty.</summary>
    private CellLayout[] LayoutRow(
        IReadOnlyList<IReadOnlyList<RichTextRun>> cells, IReadOnlyList<float> widths)
    {
        var result = new CellLayout[widths.Count];
        for (var j = 0; j < widths.Count; j++)
        {
            var runs = j < cells.Count ? cells[j] : Array.Empty<RichTextRun>();
            var layout = RichTextLayout.Layout(_canvas, runs, widths[j]);
            result[j] = new CellLayout(runs, layout, SegmentTexts(runs, layout));
        }
        return result;
    }

    private static float ContentHeight(CellLayout[] cells)
    {
        var height = 0f;
        for (var j = 0; j < cells.Length; j++)
        {
            if (cells[j].Layout.Height > height)
                height = cells[j].Layout.Height;
        }
        return height;
    }

    private static string[] SegmentTexts(IReadOnlyList<RichTextRun> runs, RichTextLayoutResult layout)
    {
        var count = 0;
        foreach (var line in layout.Lines)
            count += line.Segments.Count;
        if (count == 0)
            return Array.Empty<string>();

        var texts = new string[count];
        var t = 0;
        foreach (var line in layout.Lines)
        {
            foreach (var seg in line.Segments)
            {
                var text = runs[seg.RunIndex].Text;
                texts[t++] = seg.Start == 0 && seg.Length == text.Length
                    ? text
                    : text.Substring(seg.Start, seg.Length);
            }
        }
        return texts;
    }

    /// <summary>The min-content table width: Σ column min-content + 2 × <see cref="CellPaddingX"/>
    /// per column — the view's intrinsic width (see the class doc).</summary>
    private float MinTableWidth()
    {
        if (!ReferenceEquals(_minForHeader, _header)
            || !ReferenceEquals(_minForRows, _rows)
            || !ReferenceEquals(_minForAlignments, _alignments))
        {
            var columnCount = _alignments.Count;
            var sum = 0f;
            for (var j = 0; j < columnCount; j++)
            {
                var min = j < _header.Count ? TableLayout.MinContentWidth(_canvas, _header[j]) : 0f;
                for (var i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (j < row.Count)
                        min = Math.Max(min, TableLayout.MinContentWidth(_canvas, row[j]));
                }
                sum += min;
            }
            _minTableWidth = columnCount > 0 ? sum + 2f * CellPaddingX * columnCount : 0f;
            _minForHeader = _header;
            _minForRows = _rows;
            _minForAlignments = _alignments;
        }
        return _minTableWidth;
    }

    /// <summary>Draws one row's cells with their tops at <paramref name="rowTop"/> −
    /// <see cref="CellPaddingY"/> (cells are top-aligned within the row), each wrapped line offset
    /// within its column's content box per the column's alignment.</summary>
    private void DrawRow(ICanvas c, Solved solved, CellLayout[] cells, float rowTop, int z)
    {
        var textTop = rowTop - CellPaddingY;
        var x = Position.Left + CellPaddingX;
        for (var j = 0; j < cells.Length; j++)
        {
            var columnWidth = solved.Columns.Widths[j];
            var cell = cells[j];
            var lineTop = textTop;
            var segText = 0;
            foreach (var line in cell.Layout.Lines)
            {
                var bottom = lineTop - line.Height;
                var offset = AlignmentOffset(solved.Columns.Alignments[j], columnWidth, line.Width);
                foreach (var seg in line.Segments)
                {
                    var run = cell.Runs[seg.RunIndex];
                    var rect = new RectF(x + offset + seg.X, bottom, seg.Width, line.Height);

                    if (run.IsCode && CodeChipBackground != 0)
                    {
                        _chipStyle.BackgroundColor = CodeChipBackground;
                        c.DrawRect(new DrawRectInputs
                        {
                            Position = rect,
                            Style = _chipStyle,
                            ZIndex = z, // strictly below the segment's text
                        });
                    }

                    if (run.Underline)
                    {
                        var underlineY = bottom + UnderlineThickness;
                        c.DrawLine(new DrawLineInputs
                        {
                            Start = new PointF(rect.Left, underlineY),
                            End = new PointF(rect.Right, underlineY),
                            Thickness = UnderlineThickness,
                            Color = run.Style.TextColor.Value,
                            ZIndex = z + 1,
                        });
                    }

                    c.DrawText(new DrawTextInputs
                    {
                        Position = rect,
                        Text = cell.SegmentTexts[segText++],
                        Style = run.Style,
                        ZIndex = z + 1,
                    });
                }
                lineTop = bottom;
            }
            x += columnWidth + 2f * CellPaddingX;
        }
    }

    private static float AlignmentOffset(ColumnAlignment alignment, float columnWidth, float lineWidth) =>
        alignment switch
        {
            ColumnAlignment.Right => columnWidth - lineWidth,
            ColumnAlignment.Center => (columnWidth - lineWidth) / 2f,
            _ => 0f,
        };

    /// <summary>Draws a horizontal rule whose band starts at <paramref name="top"/> and extends
    /// <paramref name="thickness"/> downward, spanning the table's drawn width.</summary>
    private static void DrawRule(
        ICanvas c, float left, float top, float width, float thickness, uint color, int z)
    {
        var y = top - thickness / 2f;
        c.DrawLine(new DrawLineInputs
        {
            Start = new PointF(left, y),
            End = new PointF(left + width, y),
            Thickness = thickness,
            Color = color,
            ZIndex = z,
        });
    }
}
