using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed class GroupSectionViewModel : IDisposable
{
    private readonly Group _group;
    private readonly IRepoRegistry _registry;

    private readonly Derived<IReadOnlyList<Repo>> _visiblePrimaries;

    public Guid GroupId => _group.Id;
    public GroupHeaderRowViewModel HeaderVm { get; }

    // Group.RepoIds holds primary IDs only — worktrees and submodules nest under their
    // parent via RepoEntry. Collapsed groups still surface the active row's primary so
    // the user can see "where they are" when the rest of the group is hidden.
    public IReadable<IReadOnlyList<Repo>> VisiblePrimaries => _visiblePrimaries;

    public GroupSectionViewModel(Group group, IRepoRegistry registry, Command newGroup)
    {
        _group = group;
        _registry = registry;
        HeaderVm = new GroupHeaderRowViewModel(group, registry, newGroup);
        _visiblePrimaries = new Derived<IReadOnlyList<Repo>>(ComputeVisiblePrimaries);
    }

    private IReadOnlyList<Repo> ComputeVisiblePrimaries()
    {
        var reposById = _registry.Repos.ToDictionary(r => r.Id);
        var result = new List<Repo>();

        if (_group.IsCollapsed.Value)
        {
            var active = _registry.Active.Value;
            if (active is null) return result;
            var primaryId = active.ParentRepoId ?? active.Id;
            foreach (var repoId in _group.RepoIds)
            {
                if (repoId == primaryId && reposById.TryGetValue(repoId, out var repo))
                    result.Add(repo);
            }
            return result;
        }

        foreach (var repoId in _group.RepoIds)
        {
            if (reposById.TryGetValue(repoId, out var repo) && repo.IsPrimary)
                result.Add(repo);
        }
        return result;
    }

    public void Dispose()
    {
        _visiblePrimaries.Dispose();
        HeaderVm.Dispose();
    }
}
