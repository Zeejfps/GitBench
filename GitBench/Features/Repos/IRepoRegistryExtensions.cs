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

    public static List<Repo> PrimariesIn(this IRepoRegistry registry, Group group)
    {
        var reposById = registry.Repos.ToDictionary(r => r.Id);
        var result = new List<Repo>();
        foreach (var repoId in group.RepoIds)
        {
            if (reposById.TryGetValue(repoId, out var repo) && repo.IsPrimary)
                result.Add(repo);
        }
        return result;
    }
}
