namespace GitGui;

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Head,
    Stash,
}

public readonly record struct RefBadge(string Name, RefKind Kind);

public readonly record struct ParentLink(int ParentIndex, int Lane);

public sealed record CommitNode(
    string Sha,
    string Summary,
    string Author,
    DateTimeOffset When,
    IReadOnlyList<string> ParentShas,
    int Lane,
    bool HasIncomingAtCommitLane,
    IReadOnlyList<ParentLink> InWalkParentLanes,
    IReadOnlyList<int> IncomingLanes,
    IReadOnlyList<int> PassThroughLanes,
    IReadOnlyList<RefBadge> Refs);

public sealed record CommitSnapshot(
    Guid RepoId,
    string RepoPath,
    IReadOnlyList<CommitNode> Commits,
    int LaneCount,
    bool Truncated,
    string? ErrorMessage,
    string? HeadBranchName = null);
