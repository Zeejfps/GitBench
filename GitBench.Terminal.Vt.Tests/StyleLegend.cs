namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Assigns one character to each distinct cell style in a frame, so the style plane of a snapshot
/// is exactly as wide as its text plane and the two line up column for column.
/// </summary>
/// <remarks>
/// Letters are handed out in a sorted order — foreground, then background, then attributes —
/// rather than in order of first appearance. Order of appearance would renumber the whole legend
/// whenever a program painted its screen in a different order; sorting means a style's letter only
/// moves when the set of styles itself changes, which is when a reader wants to look anyway.
/// </remarks>
public sealed class StyleLegend
{
    const char DefaultMark = '.';

    /// <summary>Excludes '.', '|' and space, which the format uses structurally.</summary>
    const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
        + "+=*%$#@!?<>^&/\\~,;:'\"`(){}[]-_";

    readonly Dictionary<CellStyle, char> marks;

    StyleLegend(Dictionary<CellStyle, char> marks, IReadOnlyList<string> entries)
    {
        this.marks = marks;
        Entries = entries;
    }

    public IReadOnlyList<string> Entries { get; }

    public static StyleLegend Build(IReadOnlyList<TerminalCell[]> rows)
    {
        var distinct = rows
            .SelectMany(row => row)
            .Select(cell => cell.Style())
            .Where(style => !style.IsDefault)
            .Distinct()
            .OrderBy(style => style.Foreground.SortKey())
            .ThenBy(style => style.Background.SortKey())
            .ThenBy(style => (int)style.Attributes)
            .ToList();

        if (distinct.Count > Alphabet.Length)
            throw new InvalidOperationException(
                $"A frame used {distinct.Count} distinct styles; the snapshot format has {Alphabet.Length} marks. "
                + "Widen the style plane before extending the alphabet, or the plane stops lining up with the text.");

        var marks = new Dictionary<CellStyle, char>();
        var entries = new List<string> { $"  {DefaultMark}  default" };

        for (var i = 0; i < distinct.Count; i++)
        {
            marks[distinct[i]] = Alphabet[i];
            entries.Add($"{Alphabet[i]}  {Describe(distinct[i])}");
        }

        return new StyleLegend(marks, entries);
    }

    /// <summary>
    /// The style plane for one row, trimmed to the same width as its text row and empty when every
    /// cell in it is in the default style.
    /// </summary>
    public string Row(TerminalCell[] cells)
    {
        var width = GridSnapshot.TrimmedWidth(cells);
        if (cells.Take(width).All(cell => cell.Style().IsDefault))
            return string.Empty;

        return string.Concat(cells
            .Take(width)
            .Select(cell => cell.Style() is { IsDefault: false } style ? marks[style] : DefaultMark));
    }

    static string Describe(CellStyle style)
    {
        var text = $"{style.Foreground} on {style.Background}";
        return style.Attributes == CellAttributes.None
            ? text
            : $"{text} {style.Attributes.ToString().ToLowerInvariant().Replace(", ", "+")}";
    }
}
