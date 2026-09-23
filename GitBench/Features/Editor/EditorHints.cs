using GitBench.Features.Diff;

namespace GitBench.Features.Editor;

/// <summary>A run of file lines, first to last, 1-based and inclusive.</summary>
internal readonly record struct LineSpan(int From, int To)
{
    public bool Contains(int line) => From <= line && line <= To;
}

/// <summary>What a guide has laid over one file in the editor: lines lit up, and suggested lines
/// drawn after a line of it.</summary>
internal sealed record EditorHints(string Path, IReadOnlyList<LineSpan> Spotlights, EditorGhost? Ghost);

/// <summary>Suggested code drawn after a line. A draft shrinks as the reader types it: each of its
/// lines goes once a line reading the same is in the file just below where it hangs.</summary>
internal sealed record EditorGhost(FileLine After, IReadOnlyList<string> Lines, bool ShrinksAsTyped);

/// <summary>Which lines of a draft the reader has not typed yet, and where the rest now hangs.</summary>
internal static class GhostMatch
{
    /// <summary>How far below the anchor typed lines are looked for, beyond the draft's own length.</summary>
    private const int Slack = 8;

    /// <param name="line">Reads a file line, 1-based.</param>
    public static GhostLines Remaining(EditorGhost ghost, FileLine anchor, int lineCount, Func<int, string> line)
    {
        if (!ghost.ShrinksAsTyped) return new GhostLines(anchor, ghost.Lines);

        // Typed means inside the run the reader has written: from the anchor down to the last line
        // with real content that reads as a line of the draft. A lone brace below it is the file's
        // own, not theirs.
        var end = Math.Min(lineCount, anchor.Value + ghost.Lines.Count * 2 + Slack);
        var lastTyped = anchor.Value;
        for (var i = end; i > anchor.Value; i--)
        {
            var text = line(i).Trim();
            if (!text.Any(char.IsLetterOrDigit) || !Contains(ghost.Lines, text)) continue;
            lastTyped = i;
            break;
        }

        var typed = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = anchor.Value + 1; i <= lastTyped; i++)
        {
            var text = line(i).Trim();
            if (text.Length == 0) continue;
            typed[text] = typed.GetValueOrDefault(text) + 1;
        }

        var remaining = new List<string>(ghost.Lines.Count);
        foreach (var draft in ghost.Lines)
        {
            var key = draft.Trim();
            if (key.Length > 0 && typed.TryGetValue(key, out var count) && count > 0)
            {
                typed[key] = count - 1;
                continue;
            }

            remaining.Add(draft);
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

    private static bool Contains(IReadOnlyList<string> lines, string trimmed)
    {
        foreach (var candidate in lines)
            if (candidate.Trim() == trimmed)
                return true;
        return false;
    }
}
