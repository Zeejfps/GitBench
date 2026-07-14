namespace GitBench.Git;

/// <summary>
/// Maps a hunk of one diff onto the hunks of another by the line ranges of the side the two diffs
/// share. The working-tree review shows HEAD→disk hunks, but staging/discarding must patch against
/// the index→worktree diff (same new side: the disk file), and unstaging against the HEAD→index
/// diff (same old side: HEAD) — so a displayed hunk is resolved to the target diff's hunks covering
/// the same lines of the shared side.
/// </summary>
internal static class HunkOverlap
{
    /// <summary>
    /// The inclusive new-side line range a hunk actually changes. Added lines contribute their own
    /// line numbers; a removed run contributes the position it hangs off (the last preceding
    /// context/added line, or the hunk's new start when the run opens the hunk). Context lines only
    /// advance that anchor — including them would make adjacent-but-independent hunks overlap.
    /// </summary>
    public static (int Start, int End) NewSideChangeSpan(DiffHunk hunk)
        => ChangeSpan(hunk, hunk.NewStart, oldSide: false);

    /// <summary>The old-side mirror: removed lines contribute, an added run anchors.</summary>
    public static (int Start, int End) OldSideChangeSpan(DiffHunk hunk)
        => ChangeSpan(hunk, hunk.OldStart, oldSide: true);

    private static (int Start, int End) ChangeSpan(DiffHunk hunk, int fallback, bool oldSide)
    {
        var anchor = fallback;
        var start = int.MaxValue;
        var end = int.MinValue;

        // A changed line with a number on this side extends the span at that number; one without
        // (a removal on the new side, an addition on the old) extends at the anchor it hangs off.
        foreach (var line in hunk.Lines)
        {
            var own = oldSide ? line.OldLineNumber : line.NewLineNumber;
            anchor = own ?? anchor;
            if (line.Kind != DiffLineKind.Context) Extend(anchor);
        }

        return start == int.MaxValue ? (fallback, fallback) : (start, end);

        void Extend(int line)
        {
            if (line < start) start = line;
            if (line > end) end = line;
        }
    }

    /// <summary>
    /// Indices (ascending) of the diff's hunks whose change spans intersect the given span, both
    /// measured by <paramref name="spanOf"/> — the shared side's span function.
    /// </summary>
    public static List<int> OverlappingHunks(
        DiffResult diff, (int Start, int End) span, Func<DiffHunk, (int Start, int End)> spanOf)
    {
        var result = new List<int>();
        for (var i = 0; i < diff.Hunks.Count; i++)
        {
            var candidate = spanOf(diff.Hunks[i]);
            if (candidate.Start <= span.End && span.Start <= candidate.End)
                result.Add(i);
        }

        return result;
    }

    public static bool Overlaps(
        DiffResult diff, (int Start, int End) span, Func<DiffHunk, (int Start, int End)> spanOf)
    {
        for (var i = 0; i < diff.Hunks.Count; i++)
        {
            var candidate = spanOf(diff.Hunks[i]);
            if (candidate.Start <= span.End && span.Start <= candidate.End)
                return true;
        }

        return false;
    }
}
