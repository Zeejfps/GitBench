using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>
/// A hidden new-file region between hunks: gap <c>i</c> (0…H) sits above hunk <c>i</c>, gap
/// <c>H</c> below the last hunk. Bounds are 1-based inclusive new-file line numbers. Expanded
/// lines are unchanged by definition, so their old-side number is <c>new + OldNewDelta</c>.
/// </summary>
internal readonly record struct DiffGap(int GapIndex, int NewStart, int NewEnd, int OldNewDelta)
{
    /// <summary>Hidden line count, or null while the gap is open-ended (the EOF gap before the
    /// file's line count is known).</summary>
    public int? Count => NewEnd == int.MaxValue ? null : Math.Max(0, NewEnd - NewStart + 1);
}

internal static class DiffGaps
{
    /// <summary>
    /// Per-gap bounds and old↔new deltas from hunk geometry alone. Exact for gaps 0…H−1; the
    /// EOF gap is open-ended (<c>NewEnd = int.MaxValue</c>) until <paramref name="fileLineCount"/>
    /// is supplied, which also clamps every gap to the lines actually held (truncation cap).
    /// </summary>
    public static List<DiffGap> Compute(DiffResult r, int? fileLineCount = null)
    {
        var gaps = new List<DiffGap>(r.Hunks.Count + 1);
        for (var i = 0; i <= r.Hunks.Count; i++)
        {
            var start = i == 0 ? 1 : NextNewLine(r.Hunks[i - 1]);
            var delta = i == 0 ? 0 : NextOldLine(r.Hunks[i - 1]) - NextNewLine(r.Hunks[i - 1]);
            int end;
            if (i < r.Hunks.Count)
            {
                var h = r.Hunks[i];
                // A zero-length new range (pure delete) sits *after* line NewStart, so the
                // hidden region above it still includes NewStart itself. NewStart can be 0
                // for a delete at the top of the file, which leaves gap 0 empty (end < start).
                end = h.NewLines == 0 ? h.NewStart : h.NewStart - 1;
            }
            else
            {
                end = fileLineCount ?? int.MaxValue;
            }
            if (fileLineCount is int cap && end != int.MaxValue && end > cap) end = cap;
            gaps.Add(new DiffGap(i, start, end, delta));
        }
        return gaps;
    }

    /// <summary>
    /// Whether the last hunk already touches EOF, i.e. nothing can be hidden below it. Git
    /// emits up to <see cref="DiffOptions.ContextLines"/> trailing context lines; fewer means
    /// it ran out of file. Only a heuristic until the file's line count makes gaps exact.
    /// </summary>
    public static bool LastHunkReachesEof(DiffResult r)
    {
        if (r.Hunks.Count == 0) return true;
        var lines = r.Hunks[^1].Lines;
        var trailing = 0;
        for (var i = lines.Count - 1; i >= 0 && lines[i].Kind == DiffLineKind.Context; i--)
            trailing++;
        return trailing < DiffOptions.ContextLines;
    }

    // First new/old-file line after the hunk. A zero-length range's start is the line *before*
    // the range position, so the next line is start + 1 rather than start + length.
    private static int NextNewLine(DiffHunk h) => h.NewStart + Math.Max(h.NewLines, 1);

    private static int NextOldLine(DiffHunk h) => h.OldStart + Math.Max(h.OldLines, 1);
}
