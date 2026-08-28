using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

/// <summary>
/// One maximal span of columns a renderer can draw in a single call: consecutive, one style, one
/// code point per column.
/// </summary>
internal readonly record struct TerminalRowRun(int Column, int Length, RunStyle Style);

/// <summary>
/// One row of cells in the form the renderer draws it: the row's style runs, and the code point
/// each of its columns shows.
/// </summary>
/// <remarks>
/// <para>
/// Returned whole rather than as a count of runs written, because a count leaves the trimming to
/// the caller and a forgotten <c>runs[..count]</c> is silent: a row that fills three runs of an
/// 80-column scratch buffer leaves the other 77 holding the previous row, and the view draws them.
/// <see cref="Runs"/> arrives already trimmed to this row.
/// </para>
/// <para>
/// The code points come back with it because neither half means anything alone — a run names
/// columns, and what it draws is the slice of this row's code points at those columns.
/// <see cref="CodePointsOf"/> is that slice, so the arithmetic has one home. It does not make every
/// mistake unrepresentable: a run belonging to a different live split still slices this one, and
/// nothing here catches that.
/// </para>
/// <para>
/// A ref struct, so it cannot outlive or be stored beside the buffers it points into. Those buffers
/// are the caller's per-row scratch and a row stops meaning anything the moment the next row is
/// split into the same memory.
/// </para>
/// </remarks>
internal readonly ref struct TerminalRowRuns
{
    readonly ReadOnlySpan<TerminalRowRun> _runs;
    readonly ReadOnlySpan<int> _codePoints;

    TerminalRowRuns(ReadOnlySpan<TerminalRowRun> runs, ReadOnlySpan<int> codePoints)
    {
        _runs = runs;
        _codePoints = codePoints;
    }

    /// <summary>
    /// The row's runs, ascending and contiguous, covering every column exactly once. The only
    /// statement of how far this row extends.
    /// </summary>
    public ReadOnlySpan<TerminalRowRun> Runs => _runs;

    /// <summary>The code points <paramref name="run"/> draws, one per column it covers.</summary>
    public ReadOnlySpan<int> CodePointsOf(in TerminalRowRun run) => _codePoints.Slice(run.Column, run.Length);

    /// <summary>
    /// Splits one row into style runs and fills in the code point each of its columns shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Style is whatever <paramref name="styler"/> says and nothing else. Cells whose runes differ
    /// but whose styles are equal share a run; cells whose runes are equal but whose styles differ
    /// do not. A wide character is no exception: a trailer styled unlike its leader starts a run,
    /// because a renderer that drew the two together would draw one of them in the wrong colours.
    /// </para>
    /// <para>
    /// A <see cref="CellWidth.WideTrailer"/> column carries U+0020, which is the convention
    /// <c>ICanvas.DrawGlyphRun</c> documents: the leader's glyph overhangs into the column the grid
    /// has already reserved for it, and the trailer must not draw a second one.
    /// </para>
    /// <para>
    /// A cell carrying a grapheme cluster (<see cref="TerminalCell.Combining"/>) contributes only
    /// its base rune, since a run is one code point per column and marks have no column of their
    /// own. Nothing produces one today; when something does, such a cell will need a run to itself,
    /// drawn as text rather than as a glyph run.
    /// </para>
    /// </remarks>
    /// <param name="codePoints">
    /// At least as long as <paramref name="row"/>; indices 0..row.Length-1 are written and the rest
    /// left alone.
    /// </param>
    /// <param name="runs">
    /// At least as long as <paramref name="row"/> — a row that changes style every column needs a
    /// run per column, so nothing shorter is always enough.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Either output buffer is shorter than the row. Checked before anything is written, so a row
    /// is either split whole or not at all.
    /// </exception>
    public static TerminalRowRuns Split(
        ReadOnlySpan<TerminalCell> row,
        ICellStyler styler,
        Span<int> codePoints,
        Span<TerminalRowRun> runs)
    {
        if (codePoints.Length < row.Length) throw ShorterThanRow(nameof(codePoints), codePoints.Length, row.Length);
        if (runs.Length < row.Length) throw ShorterThanRow(nameof(runs), runs.Length, row.Length);
        if (row.IsEmpty) return new TerminalRowRuns(default, default);

        codePoints[0] = ColumnCodePoint(row[0]);
        var style = styler.Style(row[0]);
        var start = 0;
        var written = 0;

        for (var column = 1; column < row.Length; column++)
        {
            ref readonly var cell = ref row[column];
            codePoints[column] = ColumnCodePoint(cell);

            var cellStyle = styler.Style(cell);
            if (cellStyle == style) continue;

            runs[written++] = new TerminalRowRun(start, column - start, style);
            start = column;
            style = cellStyle;
        }

        runs[written++] = new TerminalRowRun(start, row.Length - start, style);
        return new TerminalRowRuns(runs[..written], codePoints[..row.Length]);
    }

    static int ColumnCodePoint(in TerminalCell cell) =>
        cell.Width == CellWidth.WideTrailer ? ' ' : cell.Rune.Value;

    static ArgumentException ShorterThanRow(string name, int length, int columns) =>
        new($"A buffer of {length} cannot hold a row of {columns} columns.", name);
}
