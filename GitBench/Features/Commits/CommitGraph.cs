namespace GitBench.Features.Commits;

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
public enum RefSyncState
{
    None,
    Untracked,
    Diverged,
    InSync,
}

// IsCurrent marks the local branch that HEAD points at (the checked-out branch) so it can
// absorb the standalone "HEAD" badge (rendered with a bold name). Sync drives the branch
// glyph's color (see RefSyncState).
public readonly record struct RefBadge(string Name, RefKind Kind, bool IsCurrent = false, RefSyncState Sync = RefSyncState.None);

public readonly record struct ParentLink(int ParentIndex, int Lane);

// A lane paired with how the edge occupying it renders: Dashed follows the edge's owner (the
// stash or remote-only commit that opened it), so auxiliary chains keep their dashes across rows
// owned by other commits — both passing through them and converging into their dots.
public readonly record struct GraphLane(int Lane, bool Dashed);

public sealed record CommitNode(
    string Sha,
    string Summary,
    string Author,
    DateTimeOffset When,
    IReadOnlyList<string> ParentShas,
    int Lane,
    bool HasIncomingAtCommitLane,
    bool IncomingAtCommitLaneDashed,
    IReadOnlyList<ParentLink> InWalkParentLanes,
    IReadOnlyList<GraphLane> IncomingLanes,
    IReadOnlyList<GraphLane> PassThroughLanes,
    IReadOnlyList<RefBadge> Refs,
    // Reachable from a remote-tracking branch but from no local branch, HEAD, tag or stash.
    bool RemoteOnly = false,
    // Reachable only from remote branches with no local counterpart (no local branch tracks
    // or name-matches them). Implies RemoteOnly; the history view's remote filter hides these.
    bool UnmatchedRemoteOnly = false);

public sealed record CommitSnapshot(
    Guid RepoId,
    string RepoPath,
    IReadOnlyList<CommitNode> Commits,
    int LaneCount,
    bool Truncated,
    string? HeadBranchName = null);
