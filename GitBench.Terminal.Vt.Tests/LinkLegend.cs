namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Assigns one character to each distinct hyperlink in a frame, so the link plane of a snapshot
/// lines up column for column with its text plane.
/// </summary>
/// <remarks>
/// <para>
/// The same idea as <see cref="StyleLegend"/> and for the same reason, but the thing being named is
/// not the url: two runs of cells pointing at the same url can be two different links, and one link
/// can be split across rows. What the plane records is <em>which cells belong together</em>, which
/// is the property a renderer highlights on and the one an engine can get wrong while still
/// printing the right text.
/// </para>
/// <para>
/// Marks are handed out by url and then by where the link first appears, never by the engine's own
/// id. An id is an engine's private counter and two correct engines will number the same session
/// differently; a golden that recorded one would be recording an implementation.
/// </para>
/// </remarks>
public sealed class LinkLegend
{
    const char NoLink = '.';

    /// <summary>Excludes '.', '|' and space, which the format uses structurally.</summary>
    const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    readonly Dictionary<HyperlinkId, char> marks;

    LinkLegend(Dictionary<HyperlinkId, char> marks, IReadOnlyList<string> entries)
    {
        this.marks = marks;
        Entries = entries;
    }

    public IReadOnlyList<string> Entries { get; }

    public bool IsEmpty => marks.Count == 0;

    public static LinkLegend Build(IReadOnlyList<TerminalCell[]> rows, IReadOnlyDictionary<HyperlinkId, string> urls)
    {
        var first = new Dictionary<HyperlinkId, (int Row, int Column)>();

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                var id = rows[row][column].Hyperlink;
                if (id.IsNone || first.ContainsKey(id)) continue;
                first[id] = (row, column);
            }
        }

        var ordered = first.Keys
            .OrderBy(id => urls.TryGetValue(id, out var url) ? url : string.Empty, StringComparer.Ordinal)
            .ThenBy(id => first[id].Row)
            .ThenBy(id => first[id].Column)
            .ToList();

        if (ordered.Count > Alphabet.Length)
            throw new InvalidOperationException(
                $"A frame used {ordered.Count} distinct hyperlinks; the snapshot format has {Alphabet.Length} marks.");

        var marks = new Dictionary<HyperlinkId, char>();
        var entries = new List<string>();

        for (var i = 0; i < ordered.Count; i++)
        {
            marks[ordered[i]] = Alphabet[i];
            entries.Add($"{Alphabet[i]}  {(urls.TryGetValue(ordered[i], out var url) ? url : "~unresolved~")}");
        }

        return new LinkLegend(marks, entries);
    }

    /// <summary>
    /// The link plane for one row, trimmed like its text row and empty when no cell in it is part
    /// of a link.
    /// </summary>
    public string Row(TerminalCell[] cells)
    {
        var width = GridSnapshot.TrimmedWidth(cells);
        if (cells.Take(width).All(cell => cell.Hyperlink.IsNone))
            return string.Empty;

        return string.Concat(cells
            .Take(width)
            .Select(cell => marks.TryGetValue(cell.Hyperlink, out var mark) ? mark : NoLink));
    }
}
