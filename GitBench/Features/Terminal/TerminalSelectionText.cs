using System.Text;
using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

internal static class TerminalSelectionText
{
    public static TerminalSpan? Resolve(
        ITerminalGrid grid,
        GridPoint anchor,
        GridPoint focus,
        SelectionGranularity granularity)
    {
        var bounds = GridBounds.Of(grid);
        if (TerminalSpan.Between(anchor, focus, bounds) is not { } span) return null;

        return granularity switch
        {
            SelectionGranularity.Character => span,
            SelectionGranularity.Word => ExpandToWords(grid, span, bounds),
            SelectionGranularity.Line => ExpandToLines(grid, span, bounds),
            _ => throw new NotSupportedException($"No selection expansion for {granularity}."),
        };
    }

    public static string Build(ITerminalGrid grid, TerminalSpan span)
    {
        var bounds = GridBounds.Of(grid);
        if (TerminalSpan.Surviving(span, bounds) is not { } live) return string.Empty;

        var cells = new TerminalCell[bounds.Columns];
        var text = new StringBuilder();
        var line = new StringBuilder();

        for (var row = live.Start.Row; row <= live.End.Row; row++)
        {
            if (!TryReadRow(grid, row, bounds, cells)) continue;
            if (!live.TryColumnsOn(row, bounds.Columns, out var first, out var last)) continue;

            for (var column = first; column <= last; column++)
            {
                ref readonly var cell = ref cells[column];
                if (cell.Width == CellWidth.WideTrailer) continue;
                line.Append(cell.Text);
            }

            if (row == live.End.Row) break;

            if (ContinuesPreviousRow(grid, row + 1, bounds)) continue;

            AppendTrimmed(text, line);
            text.Append('\n');
            line.Clear();
        }

        AppendTrimmed(text, line);
        return text.ToString();
    }

    static TerminalSpan? ExpandToWords(ITerminalGrid grid, TerminalSpan span, GridBounds bounds)
    {
        var cells = new TerminalCell[bounds.Columns];

        var start = span.Start;
        if (TryReadRow(grid, start.Row, bounds, cells) && IsWord(cells, start.Column))
        {
            var column = start.Column;
            while (column > 0 && IsWord(cells, column - 1)) column--;
            start = start with { Column = column };
        }

        var end = span.End;
        if (TryReadRow(grid, end.Row, bounds, cells) && IsWord(cells, end.Column))
        {
            var column = end.Column;
            while (column < bounds.LastColumn && IsWord(cells, column + 1)) column++;
            end = end with { Column = column };
        }

        return TerminalSpan.Of(start, end, bounds);
    }

    static TerminalSpan? ExpandToLines(ITerminalGrid grid, TerminalSpan span, GridBounds bounds)
    {
        var firstRow = span.Start.Row;
        while (firstRow > bounds.FirstRow && ContinuesPreviousRow(grid, firstRow, bounds)) firstRow--;

        var lastRow = span.End.Row;
        while (lastRow < bounds.LastRow && ContinuesPreviousRow(grid, lastRow + 1, bounds)) lastRow++;

        return TerminalSpan.Of(
            new GridPoint(0, firstRow),
            new GridPoint(bounds.LastColumn, lastRow),
            bounds);
    }

    static bool TryReadRow(ITerminalGrid grid, int row, GridBounds bounds, Span<TerminalCell> destination)
    {
        if (row < bounds.FirstRow || row > bounds.LastRow) return false;

        grid.CopyRow(row, destination);
        return true;
    }

    static bool ContinuesPreviousRow(ITerminalGrid grid, int row, GridBounds bounds) =>
        row >= bounds.FirstRow && row <= bounds.LastRow && grid.ContinuesPreviousRow(row);

    static bool IsWord(ReadOnlySpan<TerminalCell> cells, int column)
    {
        if (column < 0 || column >= cells.Length) return false;

        ref readonly var cell = ref cells[column];
        if (cell.Width == CellWidth.WideTrailer) return false;

        var value = cell.Rune.Value;
        if (value > 0xFF) return true;

        var character = (char)value;
        return char.IsLetterOrDigit(character) || "_-./~:".Contains(character);
    }

    static void AppendTrimmed(StringBuilder text, StringBuilder line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ') end--;

        for (var i = 0; i < end; i++) text.Append(line[i]);
    }
}
