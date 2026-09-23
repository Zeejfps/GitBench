using GitBench.Features.Diff;

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

/// <summary>Suggested code, and where in the file it goes.</summary>
internal sealed record EditorGhost(GhostPlace Place, IReadOnlyList<string> Lines);

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
    public static GhostLines Remaining(IReadOnlyList<string> draft, FileLine anchor, FileLine below, int lineCount, Func<int, string> line)
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

        var remaining = new List<string>(draft.Count);
        foreach (var suggested in draft)
        {
            var key = suggested.Trim();
            if (key.Length > 0 && typed.TryGetValue(key, out var count) && count > 0)
            {
                typed[key] = count - 1;
                continue;
            }

            remaining.Add(suggested);
        }

        while (remaining.Count > 0 && remaining[0].Trim().Length == 0) remaining.RemoveAt(0);
        if (remaining.All(l => l.Trim().Length == 0)) remaining.Clear();
        return new GhostLines(new FileLine(lastTyped), remaining);
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
