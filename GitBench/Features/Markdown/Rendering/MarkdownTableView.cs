using GitBench.Features.Markdown.Parsing;
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

    private readonly ICanvas _canvas;

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

    protected override float MeasureWidthIntrinsic()
    {
        throw new NotImplementedException("Step 6: MarkdownTableView.MeasureWidthIntrinsic");
    }

    protected override float MeasureHeightIntrinsic(float availableWidth)
    {
        throw new NotImplementedException("Step 6: MarkdownTableView.MeasureHeightIntrinsic");
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        throw new NotImplementedException("Step 6: MarkdownTableView.OnDrawSelf");
    }
}
