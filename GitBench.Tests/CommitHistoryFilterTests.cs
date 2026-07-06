using GitBench.Features.Commits;
using Xunit;

namespace GitBench.Tests;

// The remote filter drops commits reachable only from remote branches with no local counterpart
// and reassigns lanes over the kept rows. The kept set is ancestor-closed, so parent links always
// resolve and lane assignment stays consistent.
public class CommitHistoryFilterTests
{
    private static CommitNode Node(
        string sha, string[] parents, bool remoteOnly = false, bool unmatchedRemoteOnly = false) =>
        new(
            Sha: sha,
            Summary: sha,
            Author: "author",
            When: DateTimeOffset.MinValue,
            ParentShas: parents,
            Lane: -1,
            HasIncomingAtCommitLane: false,
            IncomingAtCommitLaneDashed: false,
            InWalkParentLanes: Array.Empty<ParentLink>(),
            IncomingLanes: Array.Empty<GraphLane>(),
            PassThroughLanes: Array.Empty<GraphLane>(),
            Refs: Array.Empty<RefBadge>(),
            RemoteOnly: remoteOnly,
            UnmatchedRemoteOnly: unmatchedRemoteOnly);

    private static CommitSnapshot Snapshot(params CommitNode[] nodes) =>
        new(Guid.NewGuid(), "repo", nodes, LaneCount: 99, Truncated: false);

    [Fact]
    public void NothingFlaggedReturnsTheSameSnapshot()
    {
        var snap = Snapshot(Node("A", ["B"]), Node("B", []));
        Assert.Same(snap, CommitHistoryFilter.ExcludeUnmatchedRemotes(snap));
    }

    [Fact]
    public void DropsUnmatchedRemoteCommitsAndReassignsLanes()
    {
        // Newest first: R is the tip of a remote-only branch forked from B; A is the local head.
        var snap = Snapshot(
            Node("R", ["B"], remoteOnly: true, unmatchedRemoteOnly: true),
            Node("A", ["B"]),
            Node("B", []));

        var filtered = CommitHistoryFilter.ExcludeUnmatchedRemotes(snap);

        Assert.Equal(["A", "B"], filtered.Commits.Select(c => c.Sha));
        Assert.Equal(1, filtered.LaneCount);
        Assert.All(filtered.Commits, c => Assert.Equal(0, c.Lane));
    }

    [Fact]
    public void KeptRemoteOnlyEdgesStayDashed()
    {
        // M is origin/main one ahead of local main: remote-only but matched, so it survives the
        // filter and its edge into B keeps the dashed (auxiliary) rendering.
        var snap = Snapshot(
            Node("M", ["B"], remoteOnly: true),
            Node("R", ["B"], remoteOnly: true, unmatchedRemoteOnly: true),
            Node("B", []));

        var filtered = CommitHistoryFilter.ExcludeUnmatchedRemotes(snap);

        Assert.Equal(["M", "B"], filtered.Commits.Select(c => c.Sha));
        var b = filtered.Commits.Single(c => c.Sha == "B");
        Assert.True(b.HasIncomingAtCommitLane);
        Assert.True(b.IncomingAtCommitLaneDashed);
    }

    [Fact]
    public void MergeParentLinksSurviveReassignment()
    {
        // A merge of B and C on the local side, with an unmatched remote fork off C.
        var snap = Snapshot(
            Node("R", ["C"], remoteOnly: true, unmatchedRemoteOnly: true),
            Node("M", ["B", "C"]),
            Node("B", ["D"]),
            Node("C", ["D"]),
            Node("D", []));

        var filtered = CommitHistoryFilter.ExcludeUnmatchedRemotes(snap);

        var m = filtered.Commits.Single(c => c.Sha == "M");
        Assert.Equal(2, m.InWalkParentLanes.Count);
        Assert.Equal([0, 1], m.InWalkParentLanes.Select(p => p.ParentIndex).Order());
        Assert.Equal(2, filtered.LaneCount);
    }
}
