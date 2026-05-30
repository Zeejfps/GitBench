namespace GitGui;

// Tracks whether a local branch has an upstream and whether that upstream still exists.
// Remote-branch entries always carry Tracked (the field is meaningless for them).
public enum BranchUpstreamState
{
    Tracked,      // upstream is set and the remote ref still exists
    NeverLinked,  // no upstream configured (e.g. a freshly created local branch)
    Gone,         // upstream was configured but the remote ref has since been deleted
}

public sealed record BranchEntry(
    string Name,
    string TipSha,
    bool IsHead,
    int? AheadBy = null,
    int? BehindBy = null,
    BranchUpstreamState UpstreamState = BranchUpstreamState.Tracked,
    string? UpstreamRemote = null,
    string? UpstreamBranch = null);

public sealed record RemoteGroup(string Name, IReadOnlyList<BranchEntry> Branches);

// Index is the position in `git stash list` (0 = most recent), matching how stashes
// are referenced as `stash@{N}` on the git CLI.
public sealed record StashEntry(int Index, string Sha, string Subject);

public sealed record BranchListing(
    Guid RepoId,
    IReadOnlyList<BranchEntry> LocalBranches,
    IReadOnlyList<RemoteGroup> Remotes,
    IReadOnlyList<StashEntry> Stashes,
    string? ErrorMessage)
{
    public static BranchListing Empty(Guid repoId)
        => new(repoId, Array.Empty<BranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>(), null);
}

/// Persisted per-repo state for the branches sidebar. Missing keys default to all-open.
/// Folder keys are "local:&lt;path&gt;" or "remote:&lt;remote&gt;:&lt;path&gt;" where path is the
/// slash-separated branch-name prefix (e.g. "feature/admin").
public sealed class BranchesUiState
{
    public bool LocalOpen { get; set; } = true;
    public bool RemotesOpen { get; set; } = true;
    public bool StashesOpen { get; set; } = true;
    public Dictionary<string, bool> RemoteOpen { get; set; } = new();
    public Dictionary<string, bool> FolderOpen { get; set; } = new();

    public BranchesUiState Clone() => new()
    {
        LocalOpen = LocalOpen,
        RemotesOpen = RemotesOpen,
        StashesOpen = StashesOpen,
        RemoteOpen = new Dictionary<string, bool>(RemoteOpen),
        FolderOpen = new Dictionary<string, bool>(FolderOpen),
    };
}
