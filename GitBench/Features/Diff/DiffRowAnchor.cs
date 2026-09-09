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
