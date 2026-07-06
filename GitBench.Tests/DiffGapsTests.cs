using GitBench.Features.Diff;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The gap model feeds the hunk-context expanders: wrong bounds mean expanded rows duplicate
// hunk lines or carry wrong old-side gutter numbers, and a wrong EOF verdict shows expanders
// on files that end at the last hunk.
public class DiffGapsTests
{
    private static DiffResult Result(params DiffHunk[] hunks)
        => new(
            RepoId: Guid.Empty,
            Path: "file.txt",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: hunks,
            Truncated: false,
            ErrorMessage: null);

    private static DiffHunk Hunk(int oldStart, int oldLines, int newStart, int newLines, params DiffLine[] lines)
        => new(oldStart, oldLines, newStart, newLines, null, lines);

    private static DiffLine Ctx(int o, int n) => new(DiffLineKind.Context, o, n, "ctx");
    private static DiffLine Add(int n) => new(DiffLineKind.Added, null, n, "add");
    private static DiffLine Rem(int o) => new(DiffLineKind.Removed, o, null, "rem");

    [Fact]
    public void ComputesBoundsAndDeltasForATwoHunkDiff()
    {
        // Hunk 0 adds two lines (@@ -10,3 +10,5 @@), hunk 1 removes one (@@ -30,4 +32,3 @@).
        var r = Result(Hunk(10, 3, 10, 5), Hunk(30, 4, 32, 3));

        var gaps = DiffGaps.Compute(r);

        Assert.Equal(3, gaps.Count);
        Assert.Equal(new DiffGap(0, 1, 9, 0), gaps[0]);
        // Between the hunks: new lines 15..31 hidden; two lines were added above, so old = new − 2
        // (matching hunk 1's own starts: 30 = 32 − 2).
        Assert.Equal(new DiffGap(1, 15, 31, -2), gaps[1]);
        // EOF gap open-ended until the file line count is known.
        Assert.Equal(35, gaps[2].NewStart);
        Assert.Null(gaps[2].Count);
        Assert.Equal(34 - 35, gaps[2].OldNewDelta);
    }

    [Fact]
    public void FileLineCountClosesTheEofGapAndClampsPastTruncation()
    {
        var r = Result(Hunk(10, 3, 10, 5));

        var gaps = DiffGaps.Compute(r, fileLineCount: 40);

        Assert.Equal(new DiffGap(1, 15, 40, -2), gaps[1]);
        Assert.Equal(26, gaps[1].Count);

        // A count below a gap's end clamps it (truncation cap): the gap just stops there.
        Assert.Equal(12, DiffGaps.Compute(r, fileLineCount: 12)[1].NewEnd);
        // Interior gaps clamp too.
        Assert.Equal(6, DiffGaps.Compute(r, fileLineCount: 6)[0].NewEnd);
    }

    [Fact]
    public void PureDeleteAtTopOfFileLeavesGapZeroEmpty()
    {
        // git reports @@ -1,3 +0,0 @@ for a delete at the very top.
        var r = Result(Hunk(1, 3, 0, 0));

        var gap0 = DiffGaps.Compute(r)[0];

        Assert.Equal(0, gap0.Count);
    }

    [Fact]
    public void PureDeleteHunkKeepsNeighborGapsAligned()
    {
        // @@ -10,3 +9,0 @@: old lines 10-12 deleted after new line 9. The line below the hunk
        // is new 10 ↔ old 13.
        var r = Result(Hunk(10, 3, 9, 0));

        var gaps = DiffGaps.Compute(r);

        Assert.Equal(9, gaps[0].NewEnd);   // hidden region above still includes line 9
        Assert.Equal(10, gaps[1].NewStart);
        Assert.Equal(3, gaps[1].OldNewDelta);
    }

    [Fact]
    public void LastHunkWithFullTrailingContextMayHideLinesBelow()
    {
        var r = Result(Hunk(10, 4, 10, 4, Rem(10), Add(10), Ctx(11, 11), Ctx(12, 12), Ctx(13, 13)));

        Assert.False(DiffGaps.LastHunkReachesEof(r));
    }

    [Fact]
    public void LastHunkWithShortTrailingContextReachesEof()
    {
        var r = Result(Hunk(10, 3, 10, 3, Rem(10), Add(10), Ctx(11, 11), Ctx(12, 12)));

        Assert.True(DiffGaps.LastHunkReachesEof(r));
    }

    [Fact]
    public void NoHunksReachesEof()
    {
        Assert.True(DiffGaps.LastHunkReachesEof(Result()));
    }
}
