using GitBench.Features.Diff;
using GitBench.Theming;

namespace GitBench.Features.Editor;

/// <summary>A run of file lines, first to last, 1-based and inclusive.</summary>
internal readonly record struct LineSpan(int From, int To)
{
    public bool Contains(int line) => From <= line && line <= To;
}

/// <summary>Suggested code a guide has laid over one file in the editor, and what taking it from
/// the editor does — null where it can only be taken elsewhere.</summary>
internal sealed record EditorHints(string Path, EditorGhost Ghost, SuggestionActions? Actions = null);

/// <summary>What the pills on a suggestion do: put it in, or put it in and move on.</summary>
internal sealed record SuggestionActions(Action Accept, Action AcceptAndNext);

/// <summary>Suggested code, and where in the file it goes, with its syntax colors line by line
/// and what a language server made of its names, where those are known.</summary>
internal sealed record EditorGhost(
    GhostPlace Place,
    IReadOnlyList<string> Lines,
    IReadOnlyList<IReadOnlyList<TokenSpan>>? Spans = null,
    IReadOnlyList<DraftName>? Names = null)
{
    /// <summary>Whether the two are the same code in the same place, however they are colored or
    /// their names resolved.</summary>
    public bool SameSuggestion(EditorGhost other) => Place == other.Place && Lines.SequenceEqual(other.Lines);
}

/// <summary>A name in suggested code: the line of the suggestion it is on, 0-based, where it is
/// in that line, what it is, and what a hover over it shows, as markdown, where the server said.</summary>
internal sealed record DraftName(int Line, RawColumn Start, RawColumn End, string Text, DraftNameKind Kind, string? Docs = null);

/// <summary>What a language server made of a name in suggested code.</summary>
internal abstract record DraftNameKind
{
    private DraftNameKind() { }

    /// <summary>Declared already, at a place the reader can go to.</summary>
    public sealed record Existing(string AbsolutePath, FileLine Line) : DraftNameKind;

    /// <summary>Declared by the suggestion itself.</summary>
    public sealed record Introduced : DraftNameKind
    {
        public static readonly Introduced Instance = new();
    }

    /// <summary>Declared nowhere: still to be written.</summary>
    public sealed record Missing : DraftNameKind
    {
        public static readonly Missing Instance = new();
    }
}

/// <summary>Where suggested code goes in a file.</summary>
internal abstract record GhostPlace
{
    /// <summary>In after a line. It shrinks as the reader types it: each of its lines goes once a
    /// line reading the same is in the file just below where it hangs.</summary>
    public sealed record Insert(FileLine After) : GhostPlace;

    /// <summary>In place of a run of lines, which are lit up with the suggestion drawn under them.</summary>
    public sealed record Replace(FileLine From, FileLine To) : GhostPlace;
}

/// <summary>Which lines of a draft the reader has not typed yet, and where the rest now hangs.</summary>
internal static class GhostMatch
{
    /// <param name="below">The line that followed the anchor when the draft was laid, wherever edits
    /// have moved it since: only the lines between the two are the reader's.</param>
    /// <param name="line">Reads a file line, 1-based.</param>
    /// <param name="spans">The draft's colors, line by line, or null.</param>
    /// <param name="names">What the draft's names are, or null.</param>
    public static GhostLines Remaining(
        IReadOnlyList<string> draft,
        IReadOnlyList<IReadOnlyList<TokenSpan>>? spans,
        IReadOnlyList<DraftName>? names,
        FileLine anchor,
        FileLine below,
        int lineCount,
        Func<int, string> line)
    {
        // Lines already in the file are never taken for typed ones, however much they read like
        // the draft: a closing tag or a `return (` further down is the file's own.
        var lastTyped = Math.Clamp(below.Value - 1, anchor.Value, Math.Max(anchor.Value, lineCount));
        var typed = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = anchor.Value + 1; i <= lastTyped; i++)
        {
            var text = line(i).Trim();
            if (text.Length == 0) continue;
            typed[text] = typed.GetValueOrDefault(text) + 1;
        }

        var remaining = new List<int>(draft.Count);
        for (var i = 0; i < draft.Count; i++)
        {
            var key = draft[i].Trim();
            if (key.Length > 0 && typed.TryGetValue(key, out var count) && count > 0)
            {
                typed[key] = count - 1;
                continue;
            }

            remaining.Add(i);
        }

        while (remaining.Count > 0 && draft[remaining[0]].Trim().Length == 0) remaining.RemoveAt(0);
        if (remaining.All(i => draft[i].Trim().Length == 0)) remaining.Clear();
        return new GhostLines(
            new FileLine(lastTyped),
            remaining.Select(i => draft[i]).ToArray(),
            Spans: spans is null ? null : remaining.Select(i => i < spans.Count ? spans[i] : []).ToArray(),
            Names: names is null ? null : GhostLines.NamesByLine(names, remaining));
    }

    /// <summary>Where a line anchor stands after an edit, given the edit that would undo it: moved
    /// by the lines the edit added or took away when the edit started above it.</summary>
    public static FileLine Shift(FileLine anchor, TextEdit undo)
    {
        var first = undo.Range.Start.Line.Value;
        if (anchor.Value <= first) return anchor;
        var added = undo.Range.End.Line.Value - first;
        var removed = undo.Replacement.Count(c => c == '\n');
        return new FileLine(Math.Max(first, anchor.Value + added - removed));
    }

    /// <summary>Where the line below a draft stands after an edit, given the edit that would undo
    /// it. As <see cref="Shift"/>, except that text put in at the very start of that line goes above
    /// it: it is typed, not the file's.</summary>
    public static FileLine ShiftBelow(FileLine below, TextEdit undo)
    {
        var start = undo.Range.Start;
        if (start.Line.Value > below.Value || (start.Line.Value == below.Value && start.Column.Value > 0)) return below;
        var added = undo.Range.End.Line.Value - start.Line.Value;
        var removed = undo.Replacement.Count(c => c == '\n');
        return new FileLine(Math.Max(start.Line.Value, below.Value + added - removed));
    }
}

/// <summary>A suggestion in place of a run of lines, set against them as a diff sets its two sides:
/// the lines it removed, lit up, and the suggested lines drawn under them, each after the file
/// line it follows. The first removed line is null where nothing is removed.</summary>
internal sealed record GhostReplacement(GhostLines Ghost, ReplacedLine?[] Replaced, FileLine? FirstRemoved);

/// <summary>Sets a suggestion against the lines it replaces, line by line, so lines it keeps read
/// as the file's own and only the rest are lit up or drawn.</summary>
internal static class GhostReplace
{
    // Past this many line pairs the two sides are not matched up: the whole run reads as replaced.
    private const long MaxCells = 4_000_000;

    /// <param name="old">The lines replaced, the first of them file line <paramref name="from"/>.</param>
    /// <param name="to">The last line replaced, under which a suggestion replacing nothing hangs.</param>
    public static GhostReplacement Of(IReadOnlyList<string> old, FileLine from, FileLine to, EditorGhost ghost)
    {
        var draft = ghost.Lines;
        var kept = Kept(old, draft);

        // The file has no line above the run to hang lines from: suggested lines before the first
        // kept line go under it, and it is replaced along with them.
        if (kept.Count > 0 && kept[0] is (0, > 0)) kept.RemoveAt(0);
        kept.Add((old.Count, draft.Count));

        var replaced = new ReplacedLine?[old.Count];
        var added = new List<int>(draft.Count);
        var anchors = new List<FileLine>(draft.Count);
        var emphasis = new List<IReadOnlyList<CharRange>?>(draft.Count);
        FileLine? firstRemoved = null;
        var (i, j) = (0, 0);
        foreach (var (keptOld, keptNew) in kept)
        {
            var removed = keptOld - i;
            var adds = keptNew - j;
            var anchor = old.Count == 0 ? to : new FileLine(from.Value + keptOld - 1);
            for (var k = 0; k < Math.Max(removed, adds); k++)
            {
                IReadOnlyList<CharRange>? oldRanges = null, newRanges = null;
                if (k < removed && k < adds)
                    (oldRanges, newRanges) = IntraLineDiff.ForPair(DiffText.ExpandTabs(old[i + k]), DiffText.ExpandTabs(draft[j + k]));
                if (k < removed)
                {
                    firstRemoved ??= new FileLine(from.Value + i + k);
                    // A blank line going is not worth a red band: it is how an empty file reads.
                    replaced[i + k] = old[i + k].Trim().Length == 0 ? null : new ReplacedLine(oldRanges is { Count: > 0 } ? oldRanges : null);
                }

                if (k < adds)
                {
                    added.Add(j + k);
                    anchors.Add(anchor);
                    emphasis.Add(newRanges is { Count: > 0 } ? newRanges : null);
                }
            }

            (i, j) = (keptOld + 1, keptNew + 1);
        }

        var ghostLines = new GhostLines(
            anchors.Count > 0 ? anchors[^1] : to,
            added.Select(index => draft[index]).ToArray(),
            emphasis,
            ghost.Spans is { } spans ? added.Select(index => index < spans.Count ? spans[index] : []).ToArray() : null,
            ghost.Names is { } names ? GhostLines.NamesByLine(names, added) : null,
            anchors);
        return new GhostReplacement(ghostLines, replaced, firstRemoved);
    }

    /// <summary>The longest run of lines the two sides share, in order, as index pairs.</summary>
    private static List<(int Old, int New)> Kept(IReadOnlyList<string> old, IReadOnlyList<string> draft)
    {
        var kept = new List<(int Old, int New)>();
        var prefix = 0;
        while (prefix < old.Count && prefix < draft.Count && old[prefix] == draft[prefix]) prefix++;
        var suffix = 0;
        while (suffix < old.Count - prefix && suffix < draft.Count - prefix
               && old[old.Count - 1 - suffix] == draft[draft.Count - 1 - suffix])
            suffix++;

        for (var k = 0; k < prefix; k++) kept.Add((k, k));

        var n = old.Count - prefix - suffix;
        var m = draft.Count - prefix - suffix;
        if (n > 0 && m > 0 && (long)n * m <= MaxCells)
        {
            var lengths = new int[n + 1, m + 1];
            for (var a = n - 1; a >= 0; a--)
            for (var b = m - 1; b >= 0; b--)
                lengths[a, b] = old[prefix + a] == draft[prefix + b]
                    ? lengths[a + 1, b + 1] + 1
                    : Math.Max(lengths[a + 1, b], lengths[a, b + 1]);

            var (x, y) = (0, 0);
            while (x < n && y < m)
            {
                if (old[prefix + x] == draft[prefix + y])
                {
                    kept.Add((prefix + x, prefix + y));
                    x++;
                    y++;
                }
                // On a tie the draft's line goes in first, so it keeps the earliest of the file's
                // lines it can: a closing brace stays with the block it closes.
                else if (lengths[x + 1, y] > lengths[x, y + 1]) x++;
                else y++;
            }
        }

        for (var k = suffix; k > 0; k--) kept.Add((old.Count - k, draft.Count - k));
        return kept;
    }
}
