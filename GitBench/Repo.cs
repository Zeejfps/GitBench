using System.Text.Json.Serialization;

namespace GitGui;

public enum RepoKind
{
    Primary = 0,
    Worktree = 1,
    Submodule = 2,
}

public sealed record Repo(
    Guid Id,
    string Path,
    string DisplayName,
    Guid? ParentRepoId = null)
{
    [JsonIgnore]
    public bool IsMissing { get; init; }

    // Best-known branch this checkout points at. Refreshed by WorktreeSyncService from
    // `git worktree list`. Null on a detached HEAD or before the first discovery pass.
    // Persisted so a freshly-launched app shows the right "taken branches" set before
    // the background sync completes.
    public string? Branch { get; init; }

    // Persisted so the registry can tell at load time whether a child row is a worktree
    // or a submodule. Old state files (pre-submodule) have no field — see RepoStateStore.Load,
    // which migrates by treating any pre-existing child (ParentRepoId set) as a worktree.
    public RepoKind Kind { get; init; } = RepoKind.Primary;

    [JsonIgnore]
    public bool IsPrimary => Kind == RepoKind.Primary;

    [JsonIgnore]
    public bool IsWorktree => Kind == RepoKind.Worktree;

    [JsonIgnore]
    public bool IsSubmodule => Kind == RepoKind.Submodule;
}
