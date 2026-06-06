namespace GitBench;

public enum RefKind
{
    LocalBranch,
    RemoteBranch,
    Head,
    Stash,
    Tag,
}

// Sync of a local branch against its upstream, used to tint the branch glyph: green when
// level with the remote, amber when ahead/behind, gray when there's no upstream at all.
// None applies to non-local-branch refs (remotes, tags, stashes, detached HEAD).
public enum BranchSync
{
    None,
    Untracked,
    Diverged,
    InSync,
}

// IsCurrent marks the local branch that HEAD points at (the checked-out branch) so it can
// absorb the standalone "HEAD" badge (rendered with a bold name). Sync drives the branch
// glyph's color (see BranchSync).
public readonly record struct RefBadge(string Name, RefKind Kind, bool IsCurrent = false, BranchSync Sync = BranchSync.None);

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
