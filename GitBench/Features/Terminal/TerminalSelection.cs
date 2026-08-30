using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

internal enum SelectionGranularity : byte { Character = 0, Word = 1, Line = 2 }

internal readonly record struct GridBounds(int Columns, int Rows, int ScrollbackRows)
{
    public static GridBounds Of(ITerminalGrid grid) =>
        new(grid.Size.Columns, grid.Size.Rows, grid.ScrollbackRows);

    public int FirstRow => -ScrollbackRows;

    public int LastRow => Rows - 1;

    public int LastColumn => Columns - 1;

    public bool Holds(GridPoint point) =>
        point.Row >= FirstRow
        && point.Row <= LastRow
        && point.Column >= 0
        && point.Column <= LastColumn;
}

internal readonly record struct GridPoint(int Column, int Row)
{
    public int CompareTo(GridPoint other) =>
        Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);

    public GridPoint ClampTo(GridBounds bounds) => new(
        Math.Clamp(Column, 0, Math.Max(0, bounds.LastColumn)),
        Math.Clamp(Row, bounds.FirstRow, bounds.LastRow));
}

internal readonly record struct TerminalSpan
{
    TerminalSpan(GridPoint start, GridPoint end)
    {
        Start = start;
        End = end;
    }

    public GridPoint Start { get; }

    public GridPoint End { get; }

    public static TerminalSpan? Between(GridPoint anchor, GridPoint focus, GridBounds bounds)
    {
        if (bounds.Columns <= 0 || bounds.Rows <= 0) return null;

        var start = anchor.ClampTo(bounds);
        var end = focus.ClampTo(bounds);

        return start.CompareTo(end) <= 0
            ? new TerminalSpan(start, end)
            : new TerminalSpan(end, start);
    }

    public static TerminalSpan? Of(GridPoint start, GridPoint end, GridBounds bounds)
    {
        if (!bounds.Holds(start) || !bounds.Holds(end)) return null;

        return start.CompareTo(end) <= 0 ? new TerminalSpan(start, end) : null;
    }

    public static TerminalSpan? Shift(TerminalSpan span, int linesScrolled, GridBounds bounds)
    {
        if (linesScrolled <= 0) return Surviving(span, bounds);

        var start = span.Start with { Row = span.Start.Row - linesScrolled };
        var end = span.End with { Row = span.End.Row - linesScrolled };

        return Surviving(new TerminalSpan(start, end), bounds);
    }

    public static TerminalSpan? Surviving(TerminalSpan span, GridBounds bounds)
    {
        if (bounds.Columns <= 0 || bounds.Rows <= 0) return null;
        if (span.End.Row < bounds.FirstRow) return null;
        if (span.Start.Row > bounds.LastRow) return null;

        var start = span.Start.Row < bounds.FirstRow
            ? new GridPoint(0, bounds.FirstRow)
            : span.Start;

        var end = span.End.Row > bounds.LastRow
            ? new GridPoint(bounds.LastColumn, bounds.LastRow)
            : span.End;

        start = start.ClampTo(bounds);
        end = end.ClampTo(bounds);

        return start.CompareTo(end) <= 0 ? new TerminalSpan(start, end) : null;
    }

    public bool Contains(int column, int row) =>
        new GridPoint(column, row) is var point
        && Start.CompareTo(point) <= 0
        && point.CompareTo(End) <= 0;

    public bool TryColumnsOn(int row, int columns, out int first, out int last)
    {
        first = 0;
        last = -1;

        if (row < Start.Row || row > End.Row || columns <= 0) return false;

        first = row == Start.Row ? Start.Column : 0;
        last = row == End.Row ? End.Column : columns - 1;

        if (first > last) return false;

        first = Math.Clamp(first, 0, columns - 1);
        last = Math.Clamp(last, 0, columns - 1);
        return true;
    }

    public override string ToString() =>
        $"({Start.Column},{Start.Row})..({End.Column},{End.Row})";
}
