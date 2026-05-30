namespace GitGui;

internal readonly record struct LaneParent(int ParentIndex, int Lane);

internal readonly record struct LaneAssignment(
    int Lane,
    bool HasIncomingAtCommitLane,
    LaneParent[] InWalkParentLanes,
    int[] IncomingLanes,
    int[] PassThroughLanes);

internal static class LaneAssigner
{
    public readonly record struct Input(string Sha, IReadOnlyList<string> ParentShas);

    public static (LaneAssignment[] Assignments, int LaneCount) Assign(
        IReadOnlyList<Input> commitsInDisplayOrder)
    {
        var presentShas = new HashSet<string>(commitsInDisplayOrder.Count);
        for (var i = 0; i < commitsInDisplayOrder.Count; i++)
            presentShas.Add(commitsInDisplayOrder[i].Sha);

        var lanes = new List<string?>();
        var assignments = new LaneAssignment[commitsInDisplayOrder.Count];
        var maxLaneCount = 0;

        for (var i = 0; i < commitsInDisplayOrder.Count; i++)
        {
            var c = commitsInDisplayOrder[i];

            var expectingIndexes = new List<int>();
            for (var li = 0; li < lanes.Count; li++)
            {
                if (lanes[li] == c.Sha)
                    expectingIndexes.Add(li);
            }

            int commitLane;
            int[] incomingLanes;
            bool hasIncomingAtCommitLane;
            if (expectingIndexes.Count > 0)
            {
                commitLane = expectingIndexes[0];
                hasIncomingAtCommitLane = true;
                foreach (var li in expectingIndexes)
                    lanes[li] = null;

                if (expectingIndexes.Count > 1)
                {
                    incomingLanes = new int[expectingIndexes.Count - 1];
                    for (var k = 1; k < expectingIndexes.Count; k++)
                        incomingLanes[k - 1] = expectingIndexes[k];
                }
                else
                {
                    incomingLanes = Array.Empty<int>();
                }
            }
            else
            {
                commitLane = FindOrAllocateFreeLane(lanes);
                incomingLanes = Array.Empty<int>();
                hasIncomingAtCommitLane = false;
            }

            var passThroughList = new List<int>();
            for (var li = 0; li < lanes.Count; li++)
            {
                if (lanes[li] != null && li != commitLane)
                    passThroughList.Add(li);
            }

            var inWalkParentLanes = new List<LaneParent>(c.ParentShas.Count);
            for (var j = 0; j < c.ParentShas.Count; j++)
            {
                var parent = c.ParentShas[j];
                var parentInWalk = presentShas.Contains(parent);

                if (j == 0)
                {
                    if (parentInWalk)
                    {
                        EnsureLane(lanes, commitLane);
                        lanes[commitLane] = parent;
                        inWalkParentLanes.Add(new LaneParent(j, commitLane));
                    }
                }
                else
                {
                    if (!parentInWalk) continue;

                    var existing = -1;
                    for (var li = 0; li < lanes.Count; li++)
                    {
                        if (lanes[li] == parent)
                        {
                            existing = li;
                            break;
                        }
                    }

                    if (existing >= 0)
                    {
                        inWalkParentLanes.Add(new LaneParent(j, existing));
                    }
                    else
                    {
                        var newLane = FindOrAllocateFreeLane(lanes);
                        lanes[newLane] = parent;
                        inWalkParentLanes.Add(new LaneParent(j, newLane));
                    }
                }
            }

            assignments[i] = new LaneAssignment(
                commitLane,
                hasIncomingAtCommitLane,
                inWalkParentLanes.ToArray(),
                incomingLanes,
                passThroughList.ToArray());
            if (lanes.Count > maxLaneCount) maxLaneCount = lanes.Count;
        }

        return (assignments, maxLaneCount);
    }

    private static int FindOrAllocateFreeLane(List<string?> lanes)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] == null) return i;
        }
        lanes.Add(null);
        return lanes.Count - 1;
    }

    private static void EnsureLane(List<string?> lanes, int index)
    {
        while (lanes.Count <= index)
            lanes.Add(null);
    }
}
