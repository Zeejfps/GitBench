namespace GitGui;

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Head,
    Stash,
    Tag,
}

// IsCurrent marks the local branch that HEAD points at (the checked-out branch) so it can
// absorb the standalone "HEAD" badge. IsSynced marks a local branch sitting on the same
// commit as its tracking remote, so the duplicate remote badge folds into a single badge
// with a "synced" indicator.
public readonly record struct RefBadge(string Name, RefKind Kind, bool IsCurrent = false, bool IsSynced = false);

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
