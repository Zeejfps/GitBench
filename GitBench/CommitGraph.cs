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
// absorb the standalone "HEAD" badge (rendered with a bold name). IsTracked marks a local
// branch that has a live upstream — its branch glyph is tinted green, gray when local-only,
// mirroring the Branches view's tracking cue.
public readonly record struct RefBadge(string Name, RefKind Kind, bool IsCurrent = false, bool IsTracked = false);

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
