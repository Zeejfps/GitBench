using GitBench.Features.Commits;
using Xunit;

namespace GitBench.Tests;

// Lane geometry: up to the column cap, lanes sit at full width; beyond it they compress to fit
// the same bounded column so every lane keeps a distinct x. The old code clamped overflow lanes
// onto the final column, collapsing dots and dropping the zero-width connectors between them.
public class CommitGraphRendererTests
{
    // The lane band is the column minus its left/right padding; every lane center must land in it.
    private static float LaneBand(int laneCount)
        => CommitGraphRenderer.ColumnWidth(laneCount)
           - CommitGraphRenderer.PaddingLeft
           - CommitGraphRenderer.PaddingRight;

    [Fact]
    public void WithinCapLanesUseFullWidthSpacing()
    {
        // 16px lanes, centered: lane 0 -> 8, lane 1 -> 24, lane 4 -> 72.
        Assert.Equal(8f, CommitGraphRenderer.LaneCenterX(0f, 0, 5), 3);
        Assert.Equal(24f, CommitGraphRenderer.LaneCenterX(0f, 1, 5), 3);
        Assert.Equal(72f, CommitGraphRenderer.LaneCenterX(0f, 4, 5), 3);
    }

    [Fact]
    public void OverflowLanesGetDistinctIncreasingCenters()
    {
        const int laneCount = 24; // double the 12-lane cap

        var prev = float.NegativeInfinity;
        for (var lane = 0; lane < laneCount; lane++)
        {
            var x = CommitGraphRenderer.LaneCenterX(0f, lane, laneCount);
            Assert.True(x > prev, $"lane {lane} center {x} not greater than previous {prev}");
            prev = x;
        }
    }

    [Fact]
    public void OverflowLanesStayWithinTheBoundedColumn()
    {
        const int laneCount = 40;
        var band = LaneBand(laneCount);
        for (var lane = 0; lane < laneCount; lane++)
        {
            var x = CommitGraphRenderer.LaneCenterX(0f, lane, laneCount);
            Assert.InRange(x, 0f, band);
        }
    }

    [Fact]
    public void DistinctOverflowLanesDoNotShareAColumn()
    {
        // The regression: lanes at and past the old cap (11) collapsed onto one x.
        var a = CommitGraphRenderer.LaneCenterX(0f, 12, 24);
        var b = CommitGraphRenderer.LaneCenterX(0f, 13, 24);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ColumnWidthIsCappedAtTwelveLanes()
    {
        var atCap = CommitGraphRenderer.ColumnWidth(12);
        Assert.Equal(atCap, CommitGraphRenderer.ColumnWidth(13), 3);
        Assert.Equal(atCap, CommitGraphRenderer.ColumnWidth(100), 3);
    }
}
