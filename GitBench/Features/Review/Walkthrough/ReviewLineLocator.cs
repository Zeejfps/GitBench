using System.Text;
using GitBench.Features.Diff;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// Maps a narrator's file coordinates onto one file's flattened diff rows: which row holds a line,
/// which lines the diff holds around one it does not, which collapsed gap hides it, and the text a
/// range covers. Pure over the row set and render state, so the stacked list and the tests ask the
/// same questions of the same function.
/// </summary>
internal static class ReviewLineLocator
{
    public static DiffRowKey KeyOf(DiffLineSide side, FileLine line) =>
        side == DiffLineSide.New ? DiffRowKey.NewSide(line) : DiffRowKey.OldSide(line);

    public static FileLine? LineOf(DiffRow.Line row, DiffLineSide side) =>
        side == DiffLineSide.New ? row.NewNumber.Line : row.OldNumber.Line;

    /// <summary>The nearest lines the rows hold on <paramref name="side"/> either side of a line
    /// they do not, so a caller that guessed a number wrong can correct itself.</summary>
    public static (FileLine? Before, FileLine? After) Neighbours(DiffRowSet rows, DiffLineSide side, FileLine line)
    {
        FileLine? before = null;
        FileLine? after = null;
        foreach (var row in rows.Rows)
        {
            if (row is not DiffRow.Line l || LineOf(l, side) is not { } n) continue;
            if (n < line && (before is null || n > before.Value)) before = n;
            if (n > line && (after is null || n < after.Value)) after = n;
        }
        return (before, after);
    }

    /// <summary>The unexpanded gap that hides a line — its rows exist in the file but not in the
    /// diff — or null when no gap could reveal it. Old-side lines in a gap are unchanged, so they
    /// map onto the gap through its old↔new delta.</summary>
    public static DiffGap? GapHiding(DiffRenderState.Loaded loaded, DiffLineSide side, FileLine line)
    {
        foreach (var gap in DiffGaps.Compute(loaded.Result, loaded.Expansion?.Lines.Count))
        {
            var asNew = side == DiffLineSide.New ? line.Value : line.Value - gap.OldNewDelta;
            if (asNew >= gap.NewStart && asNew <= gap.NewEnd) return gap;
        }
        return null;
    }

    /// <summary>Whether every line of a gap is on screen — the point at which a line the gap was
    /// asked to reveal, and still does not hold, is simply not in the file.</summary>
    public static bool IsRevealed(DiffRenderState.Loaded loaded, DiffGap gap)
    {
        if (loaded.Expansion is not { } expansion) return false;
        var exact = DiffGaps.Compute(loaded.Result, expansion.Lines.Count);
        if (gap.GapIndex >= exact.Count || exact[gap.GapIndex].Count is not { } total) return false;
        return expansion.Gaps.TryGetValue(gap.GapIndex, out var shown) && shown.Top + shown.Bottom >= total;
    }

    /// <summary>The text of the lines the rows hold within a range on one side, newline-joined,
    /// as the file has it — no gutter, no +/- marker.</summary>
    public static string RangeText(DiffRowSet rows, DiffLineSide side, FileLine from, FileLine to)
    {
        var text = new StringBuilder();
        foreach (var row in rows.Rows)
        {
            if (row is not DiffRow.Line l || LineOf(l, side) is not { } n) continue;
            if (n < from || n > to) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(l.Text.Raw);
        }
        return text.ToString();
    }

    /// <summary>The first numbered line the rows hold, preferring the after side, or null for a
    /// diff with no line rows (a mode-only change).</summary>
    public static (DiffLineSide Side, FileLine Line, string Text)? FirstLine(DiffRowSet rows)
    {
        foreach (var row in rows.Rows)
        {
            if (row is not DiffRow.Line l) continue;
            if (l.NewNumber.Line is { } after) return (DiffLineSide.New, after, l.Text.Raw);
            if (l.OldNumber.Line is { } before) return (DiffLineSide.Old, before, l.Text.Raw);
        }
        return null;
    }
}
