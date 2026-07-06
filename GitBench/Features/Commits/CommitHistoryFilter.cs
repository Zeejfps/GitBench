namespace GitBench.Features.Commits;

/// <summary>
/// Projection that hides commits reachable only from remote branches with no local counterpart
/// (<see cref="CommitNode.UnmatchedRemoteOnly"/>) and reassigns graph lanes over the kept rows.
/// The kept set is ancestor-closed (reachability propagates to parents in the walk), so parent
/// links never point at a hidden commit and the reassigned lanes stay consistent.
/// </summary>
internal static class CommitHistoryFilter
{
    public static CommitSnapshot ExcludeUnmatchedRemotes(CommitSnapshot snapshot)
    {
        var kept = new List<CommitNode>(snapshot.Commits.Count);
        foreach (var node in snapshot.Commits)
            if (!node.UnmatchedRemoteOnly) kept.Add(node);
        if (kept.Count == snapshot.Commits.Count) return snapshot;

        var inputs = new LaneAssigner.Input[kept.Count];
        for (var i = 0; i < kept.Count; i++)
            inputs[i] = new LaneAssigner.Input(kept[i].Sha, kept[i].ParentShas, IsAuxiliary(kept[i]));
        var (assignments, laneCount) = LaneAssigner.Assign(inputs);

        var nodes = new CommitNode[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            var a = assignments[i];
            var links = new ParentLink[a.InWalkParentLanes.Length];
            for (var k = 0; k < links.Length; k++)
                links[k] = new ParentLink(a.InWalkParentLanes[k].ParentIndex, a.InWalkParentLanes[k].Lane);
            nodes[i] = kept[i] with
            {
                Lane = a.Lane,
                HasIncomingAtCommitLane = a.HasIncomingAtCommitLane,
                IncomingAtCommitLaneDashed = a.IncomingAtCommitLaneDashed,
                InWalkParentLanes = links,
                IncomingLanes = a.IncomingLanes,
                PassThroughLanes = a.PassThroughLanes,
            };
        }
        return snapshot with { Commits = nodes, LaneCount = laneCount };
    }

    // Mirrors the auxiliary marking in GitService.Load: stash and remote-only edges render dashed.
    private static bool IsAuxiliary(CommitNode node)
    {
        if (node.RemoteOnly) return true;
        foreach (var badge in node.Refs)
            if (badge.Kind == RefKind.Stash) return true;
        return false;
    }
}
