namespace GitGui;

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
}
