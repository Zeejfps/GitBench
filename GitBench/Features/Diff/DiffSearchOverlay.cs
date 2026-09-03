using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>One row's worth of a search hit: the part of the row it covers, in the tab-expanded
/// column space the painter draws in, and whether it is the one the reader is standing on.</summary>
internal readonly record struct SearchMark(CharRange Range, bool Current);

/// <summary>
/// Where a query's hits fall on the file in front of the painter, addressed the way it asks: by the
/// line it is drawing. Held beside the rendered rows rather than folded into them, because the
/// query changes on every keystroke while the same rows stay on screen.
/// </summary>
internal sealed class DiffSearchOverlay
{
    public static readonly DiffSearchOverlay Empty = new(FileSearchHits.None);

    private readonly Dictionary<int, List<FileSearchMatch>> _byLine = [];
    private readonly FileSearchMatch? _current;

    public DiffSearchOverlay(FileSearchHits hits)
    {
        Path = hits.Path;
        Count = hits.Count;
        _current = hits.At;

        foreach (var match in hits.Matches)
        {
            if (!_byLine.TryGetValue(match.Line.Value, out var list)) _byLine[match.Line.Value] = list = [];
            list.Add(match);
        }
    }

    public string Path { get; }

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Whether these hits are the ones for the file being drawn. A body swaps files while
    /// a scan is still in flight, and hits from the file before it would land on the wrong lines.</summary>
    public bool IsFor(string path) => Count > 0 && Path == path;

    /// <summary>The parts of one drawn line its hits cover. Empty when nothing on the list touches
    /// it.</summary>
    public IReadOnlyList<SearchMark> MarksOn(FileLine line, DiffLineText text)
    {
        if (!_byLine.TryGetValue(line.Value, out var list)) return [];

        var marks = new List<SearchMark>(list.Count);
        foreach (var match in list)
        {
            var left = text.ToExpanded(match.Start);
            var right = text.ToExpanded(match.End);
            if (right <= left) continue;
            marks.Add(new SearchMark(
                new CharRange(left.Value, right.Value - left.Value),
                _current is { } cursor && cursor == match));
        }
        return marks;
    }
}
