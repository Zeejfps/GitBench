using GitBench.Git;
using GitBench.Theming;

namespace GitBench.Features.Diff;

/// <summary>How both whole-file streams — the flattened preview and the document being edited —
/// row a single after-side line, so highlighting, fold chips and widths agree between them.</summary>
internal static class FullFileRow
{
    /// <summary>What a collapsed fold leaves behind, appended to the declaration's own last line —
    /// the whole body including its braces, so a folded declaration reads as one line.</summary>
    public const string FoldChipText = "{...}";

    /// <summary>What a collapsed region leaves behind: only the lines between its first and last,
    /// the last either pulled up behind the chip or kept on a row of its own.</summary>
    public const string RegionChipText = "...";

    /// <summary>The words on the pill itself; a joined closing line is drawn after it.</summary>
    public static string ChipText(FoldChip chip) => chip switch
    {
        FoldChip.Body => FoldChipText,
        FoldChip.Interior or FoldChip.Joined => RegionChipText,
        _ => throw new ArgumentOutOfRangeException(nameof(chip), chip, "Unhandled fold chip."),
    };

    public const int UsageLensCells = 16;

    public static DiffRow.Line Line(
        DiffLineKind kind,
        int lineNumber,
        string raw,
        DiffHighlight? highlight,
        IReadOnlyList<CharRange>? emphasis,
        FoldMark? mark)
    {
        var spans = highlight?.ForLine(DiffLineKind.Context, null, lineNumber);
        if (spans is { Count: 0 }) spans = null;
        if (mark is { Chip: FoldChip.Joined { Spans: null } joined } && highlight is not null)
            mark = mark.Value with { Chip = joined with { Spans = ClosingSpans(highlight, joined) } };
        return new DiffRow.Line(
            kind, DiffGutterNumber.None, DiffGutterNumber.Of(new FileLine(lineNumber)), DiffLineText.Of(raw), spans, emphasis, mark);
    }

    /// <summary>The pill's own width, which is what a click on the chip is measured against.</summary>
    public static int PillCells(FoldChip chip) => DiffText.VisualCells(ChipText(chip));

    /// <summary>Everything a collapsed fold adds after its row's text: the pill, and any closing
    /// line pulled up behind it.</summary>
    public static int ChipCells(FoldMark? mark) => mark?.Chip switch
    {
        null => 0,
        FoldChip.Joined joined => PillCells(joined) + DiffText.VisualCells(joined.Text),
        { } chip => PillCells(chip),
    };

    // The closing line's spans moved into the trimmed text's own columns, dropping the indent they
    // began in and whatever trailing space the trim took.
    private static IReadOnlyList<TokenSpan>? ClosingSpans(DiffHighlight highlight, FoldChip.Joined joined)
    {
        var spans = highlight.ForLine(DiffLineKind.Context, null, joined.Closing.Value);
        if (spans.Count == 0) return null;

        var shifted = new List<TokenSpan>(spans.Count);
        foreach (var span in spans)
        {
            var start = Math.Max(0, span.Start - joined.Indent);
            var end = Math.Min(joined.Text.Length, span.Start + span.Length - joined.Indent);
            if (end > start) shifted.Add(span with { Start = start, Length = end - start });
        }
        return shifted.Count == 0 ? null : shifted;
    }

    public static int LensCells(DiffRow.Lens lens) => lens.Indent + UsageLensCells;

    public static int GutterDigits(int lineCount)
    {
        var digits = 1;
        while (lineCount >= 10) { digits++; lineCount /= 10; }
        return digits;
    }
}
