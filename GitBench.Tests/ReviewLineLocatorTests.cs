using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Mapping a narrator's file coordinates onto a diff's rows: an exact hit on either side, the
/// neighbours named for a miss, and which gap would have to open to show a line the rows hide.
/// </summary>
public class ReviewLineLocatorTests
{
    private static readonly ILocalizationService Loc = new LocalizationService(new State<Locale>(Locale.En));

    [Fact]
    public void ALineTheRowsHoldResolvesToItsRowOnEitherSide()
    {
        var rows = Rows(TwoHunks());

        var added = rows.RowFor(ReviewLineLocator.KeyOf(DiffLineSide.New, new FileLine(2)));
        var removed = rows.RowFor(ReviewLineLocator.KeyOf(DiffLineSide.Old, new FileLine(2)));

        Assert.NotNull(added);
        Assert.NotNull(removed);
        Assert.NotEqual(added, removed);
        Assert.Equal("two-after", ReviewLineLocator.RangeText(rows, DiffLineSide.New, new FileLine(2), new FileLine(2)));
        Assert.Equal("two-before", ReviewLineLocator.RangeText(rows, DiffLineSide.Old, new FileLine(2), new FileLine(2)));
    }

    // Line 2 was replaced, so on the old side it is the removed text and on the new side the
    // added one — the same number means two different lines, and a range across the change reads
    // each side's own text.
    [Fact]
    public void ARangeReadsOnlyTheLinesOfItsSide()
    {
        var rows = Rows(TwoHunks());

        Assert.Equal("one\ntwo-after\nthree", ReviewLineLocator.RangeText(rows, DiffLineSide.New, new FileLine(1), new FileLine(3)));
        Assert.Equal("one\ntwo-before\nthree", ReviewLineLocator.RangeText(rows, DiffLineSide.Old, new FileLine(1), new FileLine(3)));
    }

    // Line 30 is in the unexpanded gap between the hunks: not a row, but the gap that hides it is
    // named, and the nearest lines on either side are 3 and 60.
    [Fact]
    public void ALineInAGapNamesTheGapAndItsNeighbours()
    {
        var loaded = TwoHunks();
        var rows = Rows(loaded);

        Assert.Null(rows.RowFor(ReviewLineLocator.KeyOf(DiffLineSide.New, new FileLine(30))));
        var gap = Assert.NotNull(ReviewLineLocator.GapHiding(loaded, DiffLineSide.New, new FileLine(30)));
        Assert.Equal(1, gap.GapIndex);
        Assert.False(ReviewLineLocator.IsRevealed(loaded, gap));

        var (before, after) = ReviewLineLocator.Neighbours(rows, DiffLineSide.New, new FileLine(30));
        Assert.Equal(new FileLine(3), before);
        Assert.Equal(new FileLine(60), after);
    }

    // The gap's lines are unchanged, so an old-side number maps onto it through the delta: the
    // second hunk starts at old 60 / new 60, so the gap is the same on both sides here.
    [Fact]
    public void AnOldSideLineInAGapMapsThroughTheGapsDelta()
    {
        var loaded = TwoHunks();

        var gap = Assert.NotNull(ReviewLineLocator.GapHiding(loaded, DiffLineSide.Old, new FileLine(30)));
        Assert.Equal(1, gap.GapIndex);
    }

    [Fact]
    public void AGapWhoseEveryLineIsShownCountsAsRevealed()
    {
        var loaded = TwoHunks();
        var lines = Enumerable.Range(1, 120).Select(n => "// line " + n).ToArray();
        var gap = Assert.NotNull(ReviewLineLocator.GapHiding(loaded, DiffLineSide.New, new FileLine(30)));

        var partly = loaded with
        {
            Expansion = new ContextExpansion(lines, false, new Dictionary<int, GapShown> { [1] = new(10, 0) }),
        };
        Assert.False(ReviewLineLocator.IsRevealed(partly, gap));
        Assert.Null(Rows(partly).RowFor(ReviewLineLocator.KeyOf(DiffLineSide.New, new FileLine(30))));

        var fully = loaded with
        {
            Expansion = new ContextExpansion(lines, false, new Dictionary<int, GapShown> { [1] = new(56, 0) }),
        };
        Assert.True(ReviewLineLocator.IsRevealed(fully, gap));
        Assert.NotNull(Rows(fully).RowFor(ReviewLineLocator.KeyOf(DiffLineSide.New, new FileLine(30))));
    }

    // Past the last hunk everything is the open-ended EOF gap until the file's length is known.
    [Fact]
    public void ALinePastTheLastHunkIsInTheEofGap()
    {
        var loaded = TwoHunks();

        var gap = Assert.NotNull(ReviewLineLocator.GapHiding(loaded, DiffLineSide.New, new FileLine(500)));
        Assert.Equal(2, gap.GapIndex);
        var (before, after) = ReviewLineLocator.Neighbours(Rows(loaded), DiffLineSide.New, new FileLine(500));
        Assert.Equal(new FileLine(109), before);
        Assert.Null(after);
    }

    [Fact]
    public void TheFirstLinePrefersTheAfterSide()
    {
        var first = Assert.NotNull(ReviewLineLocator.FirstLine(Rows(TwoHunks())));

        Assert.Equal(DiffLineSide.New, first.Side);
        Assert.Equal(new FileLine(1), first.Line);
        Assert.Equal("one", first.Text);
    }

    private static DiffRowSet Rows(DiffRenderState state) => DiffRowSet.Build(state, Loc);

    private static DiffRenderState.Loaded TwoHunks() => new(new DiffResult(
        RepoId: Guid.Empty,
        Path: "src/Runner.cs",
        OldPath: null,
        Side: DiffSide.Range,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks:
        [
            new DiffHunk(1, 3, 1, 3, null, [
                new DiffLine(DiffLineKind.Context, 1, 1, "one"),
                new DiffLine(DiffLineKind.Removed, 2, null, "two-before"),
                new DiffLine(DiffLineKind.Added, null, 2, "two-after"),
                new DiffLine(DiffLineKind.Context, 3, 3, "three"),
            ]),
            new DiffHunk(60, 50, 60, 50, null, [
                .. Enumerable.Range(60, 50)
                    .Select(n => new DiffLine(DiffLineKind.Context, n, n, "// line " + n)),
            ]),
        ],
        Truncated: false,
        ErrorMessage: null));
}
