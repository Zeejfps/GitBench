using ZGF.Observable;

namespace GitGui;

internal sealed class GroupSectionViewModel : IDisposable
{
    private readonly Group _group;
    private readonly IRepoRegistry _registry;

    public Guid GroupId => _group.Id;
    public GroupHeaderRowViewModel HeaderVm { get; }

    public GroupSectionViewModel(Group group, IRepoRegistry registry, Command newGroup)
    {
        _group = group;
        _registry = registry;
        HeaderVm = new GroupHeaderRowViewModel(group, registry, newGroup);
    }

    // Group.RepoIds holds primary IDs only — worktrees and submodules nest under their
    // parent via RepoEntry. Collapsed groups still surface the active row's primary so
    // the user can see "where they are" when the rest of the group is hidden.
    public IEnumerable<Repo> VisiblePrimaries()
    {
        var reposById = _registry.Repos.ToDictionary(r => r.Id);

        if (_group.IsCollapsed)
        {
            var active = _registry.Active.Value;
            if (active is null) yield break;
            var primaryId = active.ParentRepoId ?? active.Id;
            foreach (var repoId in _group.RepoIds)
            {
                if (repoId == primaryId && reposById.TryGetValue(repoId, out var repo))
                    yield return repo;
            }
            yield break;
        }

        foreach (var repoId in _group.RepoIds)
        {
            if (reposById.TryGetValue(repoId, out var repo) && repo.IsPrimary)
                yield return repo;
        }
    }

    public void Dispose() => HeaderVm.Dispose();
}
