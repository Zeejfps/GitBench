namespace GitBench.Features.Diff;

/// <summary>Which side of the change numbers a row.</summary>
internal enum DiffLineSide { Old, New }

/// <summary>What names one text row across a rebuild of the row stream: a line number, on one
/// side of the change.</summary>
internal readonly record struct DiffRowKey
{
    private DiffRowKey(DiffLineSide side, FileLine line)
    {
        Side = side;
        Line = line;
    }

    public static DiffRowKey NewSide(FileLine line) => new(DiffLineSide.New, line);

    public static DiffRowKey OldSide(FileLine line) => new(DiffLineSide.Old, line);

    public DiffLineSide Side { get; }

    public FileLine Line { get; }
}

/// <summary>Where a row sits in a stream, in terms that outlive the stream: the text row it hangs
/// from (null for chrome no text row precedes), and how far below it — 0 being that row itself.</summary>
internal readonly record struct DiffRowAnchor(DiffRowKey? Line, int RowsBelow);

/// <summary>What a row stream exposes for anchors to be taken and resolved over it.</summary>
internal interface IAnchoredRows
{
    int RowCount { get; }
    DiffRowKey? KeyAt(int row);
    RowIndex? RowFor(DiffRowKey key);
}

internal static class DiffRowAnchors
{
    /// <summary>Where a row sits, in terms that survive the stream being rebuilt. Unnumbered rows
    /// hang off the last keyed row above them.</summary>
    public static DiffRowAnchor? AnchorAt(this IAnchoredRows rows, RowIndex row)
    {
        if (row.Value < 0 || row.Value >= rows.RowCount) return null;
        for (var i = row.Value; i >= 0; i--)
            if (rows.KeyAt(i) is { } key) return new DiffRowAnchor(key, row.Value - i);
        return new DiffRowAnchor(null, row.Value + 1);
    }

    /// <summary>The row an anchor names, or null when the stream does not have it. An anchor hanging
    /// below its line clamps to the run of unkeyed rows actually under it.</summary>
    public static RowIndex? RowAt(this IAnchoredRows rows, DiffRowAnchor anchor)
    {
        var count = rows.RowCount;
        if (count == 0) return null;

        var line = -1;
        if (anchor.Line is { } key)
        {
            if (rows.RowFor(key) is not { } row) return null;
            if (anchor.RowsBelow == 0) return row;
            line = row.Value;
        }

        var run = 0;
        while (line + 1 + run < count && rows.KeyAt(line + 1 + run) is null) run++;
        return new RowIndex(Math.Max(0, line + Math.Min(anchor.RowsBelow, run)));
    }
}
