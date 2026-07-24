namespace GitBench.Features.Branches;

public sealed record BranchSync(int Ahead, int Behind);

// Upstream link for a local branch that is not checked out. Tracked always carries both names and
// a count pair, so there are no nullable-field combinations to get wrong.
public abstract record LocalUpstream
{
    public sealed record None : LocalUpstream;
    public sealed record Gone : LocalUpstream;
    public sealed record Tracked(string Remote, string Branch, BranchSync Sync) : LocalUpstream;
}

// Whether HEAD has an upstream ref at all. Deliberately not a count: how far apart they are is
// owned by IRepoStatusStore, which observes it in the same git read that drives the toolbar.
public enum HeadUpstreamState { None, Gone, Tracked }

/// One local branch as `git for-each-ref` reports it. The checked-out branch is a distinct case with
/// no count field, so nothing in the tree can hold a second copy of HEAD's ahead/behind.
public abstract record LocalBranchEntry(string Name, string TipSha)
{
    public sealed record Head(string Name, string TipSha, HeadUpstreamState Upstream)
        : LocalBranchEntry(Name, TipSha);

    public sealed record Other(string Name, string TipSha, LocalUpstream Upstream)
        : LocalBranchEntry(Name, TipSha);
}

public sealed record RemoteBranchEntry(string Name, string TipSha);

public sealed record RemoteGroup(string Name, IReadOnlyList<RemoteBranchEntry> Branches);

// Index is the position in `git stash list` (0 = most recent), matching how stashes
// are referenced as `stash@{N}` on the git CLI.
public sealed record StashEntry(int Index, string Sha, string Subject);

public sealed record BranchListing(
    Guid RepoId,
    IReadOnlyList<LocalBranchEntry> LocalBranches,
    IReadOnlyList<RemoteGroup> Remotes,
    IReadOnlyList<StashEntry> Stashes)
{
    public static BranchListing Empty(Guid repoId)
        => new(repoId, Array.Empty<LocalBranchEntry>(), Array.Empty<RemoteGroup>(), Array.Empty<StashEntry>());
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
