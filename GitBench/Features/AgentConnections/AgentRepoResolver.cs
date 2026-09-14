using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;

namespace GitBench.Features.AgentConnections;

/// <summary>Which open repository a call addresses, or why none does.</summary>
internal abstract record RepoResolution
{
    public sealed record Resolved(Repo Repo) : RepoResolution;

    /// <summary>The name or path given matches no open repository.</summary>
    public sealed record Unknown(string Given, IReadOnlyList<Repo> Open) : RepoResolution;

    /// <summary>Nothing was named and there is no repository to fall back to.</summary>
    public sealed record NothingOpen : RepoResolution;
}

/// <summary>Where a session's default repository comes from when a call names none.</summary>
internal abstract record RepoDefault
{
    /// <summary>The session has driven a review window; its repository is the one meant.</summary>
    public sealed record LastDriven(Repo Repo) : RepoDefault;

    public sealed record None : RepoDefault;
}

/// <summary>
/// Maps a call's <c>repo</c> argument to an open repository: by display name, or by a path that is
/// one of the open repositories' paths or lies inside one — the deepest match wins, so a path in a
/// worktree selects the worktree over its parent. With no argument, the session's last-driven
/// repository, then the most recently opened review window's, then the main window's active one.
/// UI thread only: the registry and the window list live there.
/// </summary>
internal sealed class AgentRepoResolver
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IRepoRegistry _registry;
    private readonly IReviewWindowRegistry _windows;

    public AgentRepoResolver(IRepoRegistry registry, IReviewWindowRegistry windows)
    {
        _registry = registry;
        _windows = windows;
    }

    public RepoResolution Resolve(string? given, RepoDefault fallback)
    {
        if (!string.IsNullOrWhiteSpace(given)) return Named(given.Trim());

        if (Default(fallback) is { } repo) return new RepoResolution.Resolved(repo);
        return new RepoResolution.NothingOpen();
    }

    private RepoResolution Named(string given)
    {
        var open = _registry.Repos;
        foreach (var repo in open)
            if (string.Equals(repo.DisplayName, given, StringComparison.OrdinalIgnoreCase))
                return new RepoResolution.Resolved(repo);

        var path = RealPath.Of(given);
        Repo? deepest = null;
        foreach (var repo in open)
        {
            var root = RealPath.Of(repo.Path);
            if (!IsSameOrInside(path, root)) continue;
            if (deepest is null || root.Length > RealPath.Of(deepest.Path).Length) deepest = repo;
        }

        return deepest is { } found
            ? new RepoResolution.Resolved(found)
            : new RepoResolution.Unknown(given, open.ToArray());
    }

    private Repo? Default(RepoDefault fallback)
    {
        switch (fallback)
        {
            case RepoDefault.LastDriven driven:
                // Still open: a removed repository is no default.
                foreach (var repo in _registry.Repos)
                    if (repo.Id == driven.Repo.Id) return repo;
                break;
            case RepoDefault.None:
                break;
            default:
                throw new InvalidOperationException($"Unhandled default {fallback.GetType().Name}.");
        }

        var windows = _windows.Windows;
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            foreach (var repo in _registry.Repos)
                if (repo.Id == windows[i].Session.RepoId) return repo;
        }

        return _registry.Active.Value;
    }

    private static bool IsSameOrInside(string path, string root) =>
        path.Length >= root.Length
        && path.StartsWith(root, PathComparison)
        && (path.Length == root.Length
            || path[root.Length] == Path.DirectorySeparatorChar
            || path[root.Length] == Path.AltDirectorySeparatorChar);
}
