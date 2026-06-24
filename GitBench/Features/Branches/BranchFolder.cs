namespace GitBench.Features.Branches;

/// <summary>
/// A folder node in the branch tree — a slash-separated path within a
/// <see cref="BranchScope"/> (e.g. local "feature/admin", or "feature" under remote
/// "origin"). The single currency for folder operations: identity, the persisted
/// open/closed <see cref="Key"/>, and menu/expand targets all derive from it.
/// </summary>
public readonly record struct BranchFolder(BranchScope Scope, string Path)
{
    /// Stable key for the per-repo open/closed map. Namespaced by scope so a local
    /// "feature" folder and an "origin" "feature" folder never collide.
    public string Key => Scope.IsRemote ? $"remote:{Scope.RemoteName}:{Path}" : $"local:{Path}";
}
