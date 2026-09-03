using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>What the find bar is looking for: the text, and the two things that change what counts
/// as a hit.</summary>
internal readonly record struct FileSearchQuery(string Text, bool MatchCase, bool WholeWord)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// One hit, in the file's own coordinates: the line it sits on and its half-open span in that
/// line's raw characters.
/// </summary>
/// <remarks>
/// Raw rather than tab-expanded, because a hit is a fact about the file while the expansion belongs
/// to whatever is drawing it — <see cref="DiffSearchOverlay"/> converts one to the other for the
/// painter.
/// </remarks>
internal readonly record struct FileSearchMatch(FileLine Line, RawColumn Start, RawColumn End);

/// <summary>
/// What a query found in one file: every hit in reading order, and which one the reader is standing
/// on. One value rather than three slices, so nothing can read a hit list against another file's
/// path or against a cursor that moved after it was built.
/// </summary>
internal sealed record FileSearchHits(
    string Path,
    IReadOnlyList<FileSearchMatch> Matches,
    int Current,
    bool Capped)
{
    public static readonly FileSearchHits None = new(string.Empty, [], -1, false);

    public int Count => Matches.Count;

    /// <summary>The cursor's 1-based place in the list, or 0 when it stands on nothing.</summary>
    public int Ordinal => Current < 0 ? 0 : Current + 1;

    public FileSearchMatch? At => Current >= 0 && Current < Matches.Count ? Matches[Current] : null;
}

/// <summary>Plain-text search over a file's lines: the whole of what "find in file" means, with no
/// view attached to it.</summary>
internal static class FileSearch
{
    /// <summary>
    /// How many hits are collected before the scan stops. A one-character query over a large file
    /// matches hundreds of thousands of times and nobody steps through that list; past here the
    /// highlighting is still right for everything scanned so far and the count reads as a floor.
    /// </summary>
    public const int MaxMatches = 10_000;

    /// <summary>
    /// Every occurrence of <paramref name="query"/> in <paramref name="lines"/>, with the cursor on
    /// the first hit at or after <paramref name="anchor"/>. Hits above the anchor are not skipped —
    /// the cursor wraps to the top when the anchor leaves nothing below it, the same way stepping
    /// past the last hit does.
    /// </summary>
    public static FileSearchHits In(
        string path, IReadOnlyList<string> lines, FileSearchQuery query, FileLine anchor)
    {
        if (query.IsEmpty) return new FileSearchHits(path, [], -1, false);

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<FileSearchMatch>();
        var capped = false;

        for (var i = 0; i < lines.Count && !capped; i++)
        {
            var text = lines[i];
            var from = 0;
            while (from <= text.Length - query.Text.Length)
            {
                var at = text.IndexOf(query.Text, from, comparison);
                if (at < 0) break;

                var end = at + query.Text.Length;
                if (!query.WholeWord || IsWholeWord(text, at, end))
                {
                    matches.Add(new FileSearchMatch(
                        new FileLine(i + 1), new RawColumn(at), new RawColumn(end)));
                    if (matches.Count >= MaxMatches)
                    {
                        capped = true;
                        break;
                    }
                }

                // Past the hit, not one character into it: "aa" occurs once in "aaa", the way it
                // does in an editor.
                from = end;
            }
        }

        return new FileSearchHits(path, matches, CursorAt(matches, anchor), capped);
    }

    private static int CursorAt(IReadOnlyList<FileSearchMatch> matches, FileLine anchor)
    {
        for (var i = 0; i < matches.Count; i++)
            if (matches[i].Line.Value >= anchor.Value) return i;
        return matches.Count == 0 ? -1 : 0;
    }

    private static bool IsWholeWord(string text, int start, int end) =>
        (start == 0 || !IsWordChar(text[start - 1]))
        && (end >= text.Length || !IsWordChar(text[end]));

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
