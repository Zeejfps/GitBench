using GitBench.Git;

namespace GitBench.Features.Repos;

internal static class IRepoRegistryExtensions
{
    public static Group? FindGroupContaining(this IRepoRegistry registry, Guid repoId)
    {
        foreach (var group in registry.Groups)
        {
            if (group.RepoIds.Contains(repoId)) return group;
        }
        return null;
    }

    // The primary repos of a group, in membership order. Only primaries take part in a change set
    // (Locked decision #1); a group's RepoIds already hold only primaries, but this filters defensively.
    public static IReadOnlyList<Repo> PrimariesOfGroup(this IRepoRegistry registry, Group group)
    {
        var byId = new Dictionary<Guid, Repo>();
        foreach (var repo in registry.Repos) byId[repo.Id] = repo;

        var primaries = new List<Repo>(group.RepoIds.Count);
        foreach (var id in group.RepoIds)
            if (byId.TryGetValue(id, out var repo) && repo.IsPrimary) primaries.Add(repo);
        return primaries;
    }
}
