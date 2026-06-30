using GitBench.Features.Diff;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Pure-function tests for intra-line emphasis: changed-character ranges for paired lines and
// replace-block pairing within a hunk. No git, no engine — strings in, ranges out.
public class IntraLineDiffTests
{
    private static DiffLine Removed(string text) => new(DiffLineKind.Removed, 1, null, text);
    private static DiffLine Added(string text) => new(DiffLineKind.Added, null, 1, text);
    private static DiffLine Context(string text) => new(DiffLineKind.Context, 1, 1, text);

    private static IReadOnlyList<CharRange>?[] ForHunk(params DiffLine[] lines)
    {
        var expanded = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++) expanded[i] = DiffText.ExpandTabs(lines[i].Text);
        return IntraLineDiff.ForHunk(lines, expanded);
    }

    private static void AssertSortedDisjointInBounds(IReadOnlyList<CharRange> ranges, int len)
    {
        var prevEnd = 0;
        foreach (var r in ranges)
        {
            Assert.True(r.Length > 0, "ranges must be non-zero-length");
            Assert.True(r.Start >= prevEnd, "ranges must be sorted and non-overlapping");
            Assert.True(r.Start + r.Length <= len, "ranges must be in bounds");
            prevEnd = r.Start + r.Length;
        }
    }

    [Fact]
    public void SingleWordChangeMidLine_OneRangeEachSide_PrefixSuffixExcluded()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("var x = foo;", "var x = bar;");
        Assert.Equal(new[] { new CharRange(8, 3) }, oldR); // "foo"
        Assert.Equal(new[] { new CharRange(8, 3) }, newR); // "bar"
    }

    [Fact]
    public void TrailingOnlyChange()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("hello world", "hello there");
        Assert.Equal(new[] { new CharRange(6, 5) }, oldR);
        Assert.Equal(new[] { new CharRange(6, 5) }, newR);
    }

    [Fact]
    public void LeadingOnlyChange()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("xfoo", "yfoo");
        Assert.Equal(new[] { new CharRange(0, 1) }, oldR);
        Assert.Equal(new[] { new CharRange(0, 1) }, newR);
    }

    [Fact]
    public void MultipleDisjointChanges_CoalescedSortedNonOverlapping()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("foo and bar", "baz and qux");
        Assert.Equal(new[] { new CharRange(0, 3), new CharRange(8, 3) }, oldR);
        Assert.Equal(new[] { new CharRange(0, 3), new CharRange(8, 3) }, newR);
        AssertSortedDisjointInBounds(oldR, "foo and bar".Length);
        AssertSortedDisjointInBounds(newR, "baz and qux".Length);
    }

    [Fact]
    public void IdenticalLines_NoEmphasis()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("same text", "same text");
        Assert.Empty(oldR);
        Assert.Empty(newR);
    }

    [Fact]
    public void FullRewrite_BelowGate_NoEmphasis()
    {
        var (oldR, newR) = IntraLineDiff.ForPair("the quick brown fox", "lazy dog");
        Assert.Empty(oldR);
        Assert.Empty(newR);
    }

    [Fact]
    public void SharedPrefixCountsAsMatched_StaysAboveGate()
    {
        // The differing middle ("Foo"/"Bar") is short relative to the line, but only because the
        // long shared prefix counts as matched. Drop that from the gate and this would be
        // suppressed as a rewrite.
        var (oldR, newR) = IntraLineDiff.ForPair("xxxxxxxxxxFoo", "xxxxxxxxxxBar");
        Assert.Equal(new[] { new CharRange(10, 3) }, oldR);
        Assert.Equal(new[] { new CharRange(10, 3) }, newR);
    }

    [Fact]
    public void UnbalancedReplaceBlock_OnlyPairedIndexEmphasized()
    {
        // 3 removed, 1 added: only removed[0] pairs with added[0]; the extra removed lines read
        // as plain deletes (no emphasis).
        var res = ForHunk(
            Removed("foo bar"),
            Removed("plain one"),
            Removed("plain two"),
            Added("foo baz"));

        Assert.NotNull(res[0]); // removed[0] paired
        Assert.Null(res[1]);    // extra removed
        Assert.Null(res[2]);    // extra removed
        Assert.NotNull(res[3]); // added[0] paired
    }

    [Fact]
    public void MidBlockInsertion_MispairsFallUnderGate_NoFullLineNoise()
    {
        // 4 removed, 5 added with a line inserted at added index 2. Index-wise pairing shifts the
        // tail, so the shifted pairs (indices 2 and 3) are dissimilar and the gate suppresses
        // them rather than highlighting the whole line.
        var res = ForHunk(
            Removed("aaaa"),
            Removed("bbbb"),
            Removed("cccc"),
            Removed("dddd"),
            Added("aaaa"),
            Added("bbbb"),
            Added("ZZZZZZZZ"), // inserted
            Added("cccc"),
            Added("dddd"));

        Assert.All(res, Assert.Null); // identical pairs emit nothing; mispairs gated out
    }

    [Fact]
    public void TabContainingLines_RangesAlignToExpandedColumns()
    {
        // A tab expands to 4 columns, so the changed word sits at expanded column 4, not 1.
        var oldExp = DiffText.ExpandTabs("\tfoo");
        var newExp = DiffText.ExpandTabs("\tbar");
        var (oldR, newR) = IntraLineDiff.ForPair(oldExp, newExp);
        Assert.Equal(new[] { new CharRange(4, 3) }, oldR);
        Assert.Equal(new[] { new CharRange(4, 3) }, newR);
    }

    [Fact]
    public void ForHunk_CachedOnLinesReference()
    {
        var lines = new[] { Removed("foo bar"), Added("foo baz") };
        var expanded = new[] { "foo bar", "foo baz" };
        var first = IntraLineDiff.ForHunk(lines, expanded);
        var second = IntraLineDiff.ForHunk(lines, expanded);
        Assert.Same(first, second);
    }
}
