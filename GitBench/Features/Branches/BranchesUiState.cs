namespace GitBench.Features.Branches;

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
