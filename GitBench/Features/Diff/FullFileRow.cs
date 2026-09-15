using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>How both whole-file streams — the flattened preview and the document being edited —
/// row a single after-side line, so highlighting, fold chips and widths agree between them.</summary>
internal static class FullFileRow
{
    /// <summary>What a collapsed fold leaves behind, appended to the declaration's own last line —
    /// the whole body including its braces, so a folded declaration reads as one line.</summary>
    public const string FoldChipText = "{...}";

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
        return new DiffRow.Line(
            kind, DiffGutterNumber.None, DiffGutterNumber.Of(new FileLine(lineNumber)), DiffLineText.Of(raw), spans, emphasis, mark);
    }

    public static int ChipCells(FoldMark? mark) =>
        mark is { Chip: true } ? DiffText.VisualCells(FoldChipText) : 0;

    public static int LensCells(DiffRow.Lens lens) => lens.Indent + UsageLensCells;

    public static int GutterDigits(int lineCount)
    {
        var digits = 1;
        while (lineCount >= 10) { digits++; lineCount /= 10; }
        return digits;
    }
}
