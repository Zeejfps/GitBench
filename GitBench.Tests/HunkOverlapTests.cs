using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The working-tree review maps a displayed HEAD→disk hunk onto the file's index→worktree hunks by
// their new-side (disk) line ranges — both diffs share the same new side, so the ranges are
// directly comparable. These pin the span rules: additions count their own line numbers, a removed
// run counts the position it hangs off, and context lines never widen the span (else an adjacent
// but independent hunk whose context merely brushes the displayed one would get staged with it).
public class HunkOverlapTests
{
    private static DiffLine Ctx(int n) => new(DiffLineKind.Context, n, n, "ctx");
    private static DiffLine Rem(int n) => new(DiffLineKind.Removed, n, null, "old");
    private static DiffLine Add(int n) => new(DiffLineKind.Added, null, n, "new");

    private static DiffHunk Hunk(int newStart, params DiffLine[] lines)
        => new(
            newStart,
            lines.Count(l => l.Kind != DiffLineKind.Added),
            newStart,
            lines.Count(l => l.Kind != DiffLineKind.Removed),
            null,
            lines);

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

    [Fact]
    public void AddedLinesSpanTheirNewLineNumbers()
    {
        var hunk = Hunk(3, Ctx(3), Add(4), Add(5), Ctx(6));
        Assert.Equal((4, 5), HunkOverlap.NewSideChangeSpan(hunk));
    }

    [Fact]
    public void RemovalRunAnchorsOnThePrecedingLine()
    {
        var hunk = Hunk(7, Ctx(7), Rem(8), Rem(9), Ctx(8));
        Assert.Equal((7, 7), HunkOverlap.NewSideChangeSpan(hunk));
    }

    [Fact]
    public void RemovalOpeningTheHunkAnchorsOnNewStart()
    {
        var hunk = Hunk(5, Rem(5), Rem(6), Ctx(5));
        Assert.Equal((5, 5), HunkOverlap.NewSideChangeSpan(hunk));
    }

    [Fact]
    public void ContextNeverWidensTheSpan()
    {
        var hunk = Hunk(1, Ctx(1), Ctx(2), Ctx(3), Add(4), Ctx(5), Ctx(6), Ctx(7));
        Assert.Equal((4, 4), HunkOverlap.NewSideChangeSpan(hunk));
    }

    [Fact]
    public void ReplaceBlockSpansAnchorAndAdditions()
    {
        var hunk = Hunk(10, Ctx(10), Rem(11), Add(11), Add(12), Ctx(13));
        Assert.Equal((10, 12), HunkOverlap.NewSideChangeSpan(hunk));
    }

    [Fact]
    public void OldSideSpanMirrorsRemovalsAndAdditions()
    {
        // Removed lines carry old-side numbers (exact span, no anchor widening); an added run
        // anchors on the preceding old line.
        var replace = Hunk(10, Ctx(10), Rem(11), Add(11), Add(12), Ctx(13));
        Assert.Equal((11, 11), HunkOverlap.OldSideChangeSpan(replace));

        var addOnly = Hunk(3, Ctx(3), Add(4), Add(5), Ctx(6));
        Assert.Equal((3, 3), HunkOverlap.OldSideChangeSpan(addOnly));

        var removeOnly = Hunk(7, Ctx(7), Rem(8), Rem(9), Ctx(8));
        Assert.Equal((8, 9), HunkOverlap.OldSideChangeSpan(removeOnly));
    }

    [Fact]
    public void PicksIntersectingHunksInAscendingOrder()
    {
        var diff = Result(
            Hunk(2, Ctx(1), Add(2), Ctx(3)),
            Hunk(10, Ctx(9), Add(10), Add(11)),
            Hunk(20, Ctx(19), Add(20)));

        Assert.Equal(new[] { 1 }, HunkOverlap.OverlappingHunks(diff, (10, 11), HunkOverlap.NewSideChangeSpan));
        Assert.Equal(new[] { 0, 1 }, HunkOverlap.OverlappingHunks(diff, (2, 10), HunkOverlap.NewSideChangeSpan));
        Assert.Equal(new[] { 0, 1, 2 }, HunkOverlap.OverlappingHunks(diff, (1, 25), HunkOverlap.NewSideChangeSpan));
    }

    [Fact]
    public void OverlapsByOldSideWhenAsked()
    {
        // One old line replaced by three: old-side span stays (2,2) while the new side is (2,4).
        var diff = Result(Hunk(2, Ctx(1), Rem(2), Add(2), Add(3), Add(4), Ctx(5)));
        Assert.Equal(new[] { 0 }, HunkOverlap.OverlappingHunks(diff, (2, 2), HunkOverlap.OldSideChangeSpan));
        Assert.Empty(HunkOverlap.OverlappingHunks(diff, (3, 4), HunkOverlap.OldSideChangeSpan));
    }

    [Fact]
    public void DisjointSpanMatchesNothing()
    {
        var diff = Result(Hunk(2, Ctx(1), Add(2)), Hunk(20, Ctx(19), Add(20)));
        Assert.Empty(HunkOverlap.OverlappingHunks(diff, (10, 12), HunkOverlap.NewSideChangeSpan));
    }

    [Fact]
    public void ContextOnlyAdjacencyDoesNotOverlap()
    {
        var diff = Result(Hunk(5, Ctx(4), Add(5), Ctx(6), Ctx(7)));
        Assert.Empty(HunkOverlap.OverlappingHunks(diff, (6, 8), HunkOverlap.NewSideChangeSpan));
    }
}
