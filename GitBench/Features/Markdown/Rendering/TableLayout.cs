using GitBench.Features.Markdown.Parsing;
using ZGF.Gui;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>How the table's columns were fitted to the available width.</summary>
internal enum TableFitMode
{
    /// <summary>Every column sits at its max-content width; the whole table fits.</summary>
    AllMax,

    /// <summary>Columns start at min-content and share the leftover width proportionally to
    /// (max − min); the table exactly fills the available width and cells wrap.</summary>
    Distributed,

    /// <summary>Even min-content widths overflow: columns stay at min-content and the table is
    /// wider than the available width — the widget scrolls it horizontally.</summary>
    OverflowAtMin,
}

/// <summary>
/// A solved column layout. <paramref name="Widths"/> are per-column <b>content</b> widths (cell
/// padding excluded), one per column, in column order. <paramref name="Alignments"/> passes the
/// input alignments through untouched so the view has one object to render from.
/// <paramref name="TableWidth"/> is the table's total drawn width — Σ widths plus
/// 2 × cellPaddingX per column: Σmax + padding in <see cref="TableFitMode.AllMax"/>, exactly the
/// available width in <see cref="TableFitMode.Distributed"/>, and Σmin + padding (wider than
/// available) in <see cref="TableFitMode.OverflowAtMin"/>.
/// </summary>
internal sealed record TableColumns(
    IReadOnlyList<float> Widths,
    IReadOnlyList<ColumnAlignment> Alignments,
    TableFitMode Mode,
    float TableWidth);

/// <summary>
/// The pure column-sizing engine behind <see cref="MarkdownTableView"/> — CSS-table-style
/// auto layout over <see cref="RichTextLayout"/> measurements. No view state, no caching (the
/// view caches), just math, so the algorithm is pinned by <c>TableLayoutTests</c> with a fake
/// measurer.
/// <para>
/// Per-cell measures, both defined operationally against <see cref="RichTextLayout"/>'s break
/// model so a cell never wraps worse than the width the engine promised:
/// <b>max-content</b> is the unwrapped width — <c>RichTextLayout.Layout(canvas, runs, 0)</c>'s
/// <c>MaxLineWidth</c>, where only '\n' breaks; <b>min-content</b> is the width of the widest
/// <i>unbreakable chunk</i> — the widest slice of text with no break opportunity inside it under
/// the layout's rules: spaces separate chunks and belong to none, a break is allowed after the
/// separators <c>/ \ - _ . :</c> (the separator ends its chunk), wide (CJK) code points break
/// between characters, kinsoku prohibitions glue closing/opening punctuation to their neighbor,
/// a '\n' terminates a chunk, and a run seam is not a break by itself. Every slice is measured
/// per run with that run's own style, exactly like the layout. Laying a cell out at its
/// min-content width therefore never splits inside a chunk (a trailing space at a soft break may
/// hang past the column — invisible, same as CSS).
/// </para>
/// <para>
/// Fit, given the table's total <c>availableWidth</c> and the per-side horizontal cell padding:
/// per column, min = the maximum min-content over the column's cells (header included), max
/// likewise. With <c>contentAvail = availableWidth − 2 × cellPaddingX × columns</c>:
/// if Σmax ≤ contentAvail → <see cref="TableFitMode.AllMax"/>, widths = max. Else if
/// Σmin ≤ contentAvail → <see cref="TableFitMode.Distributed"/>: widths = min + (contentAvail −
/// Σmin) × (max − min) / Σ(max − min) — exact float proportions, no pixel rounding (nothing else
/// in the layout stack snaps to pixels either), so the widths sum to contentAvail exactly and a
/// column with max == min (including an empty column) never grows; a column can never overshoot
/// its max because the remainder is strictly smaller than Σ(max − min). Else →
/// <see cref="TableFitMode.OverflowAtMin"/>, widths = min. A non-positive
/// <c>availableWidth</c> means unconstrained and yields <see cref="TableFitMode.AllMax"/>.
/// </para>
/// <para>
/// Cells are rectangular by contract: <c>BasicMarkdownParser</c> pads short rows and truncates
/// long ones to the header's column count (asserted as a fixture in the tests), so the engine
/// never sees a ragged row.
/// </para>
/// </summary>
internal static class TableLayout
{
    /// <summary>The width of the widest unbreakable chunk of <paramref name="cell"/> — see the
    /// class doc for the operational definition. Empty (or all-empty-runs) cells measure 0.</summary>
    public static float MinContentWidth(ICanvas canvas, IReadOnlyList<RichTextRun> cell)
    {
        throw new NotImplementedException("Step 6: TableLayout.MinContentWidth");
    }

    /// <summary>The cell's unwrapped width: <see cref="RichTextLayout.Layout"/> at non-positive
    /// maxWidth ('\n' still breaks), widest line. Empty cells measure 0.</summary>
    public static float MaxContentWidth(ICanvas canvas, IReadOnlyList<RichTextRun> cell)
    {
        throw new NotImplementedException("Step 6: TableLayout.MaxContentWidth");
    }

    /// <summary>Solves the column widths for a table — see the class doc for the algorithm.
    /// <paramref name="alignments"/> defines the column count; <paramref name="header"/> and every
    /// row of <paramref name="rows"/> carry exactly that many cells.</summary>
    public static TableColumns Measure(
        ICanvas canvas,
        IReadOnlyList<IReadOnlyList<RichTextRun>> header,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> rows,
        IReadOnlyList<ColumnAlignment> alignments,
        float availableWidth,
        float cellPaddingX)
    {
        throw new NotImplementedException("Step 6: TableLayout.Measure");
    }
}
