using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed class GroupSectionViewModel : IDisposable
{
    private readonly Group _group;
    private readonly IRepoRegistry _registry;

    private readonly Derived<IReadOnlyList<Repo>> _primaryRepos;
    private readonly KeyedViewModelList<Repo, Guid, RepoNodeViewModel> _primaries;

    public Guid GroupId => _group.Id;
    public GroupHeaderRowViewModel HeaderVm { get; }

    // Group.RepoIds holds primary IDs only — worktrees and submodules nest under their parent via
    // RepoNode. Collapsed groups still surface the active row's primary so the user can see "where
    // they are" when the rest of the group is hidden.
    public ObservableList<RepoNodeViewModel> VisiblePrimaries => _primaries.Items;

    public GroupSectionViewModel(Group group, IRepoRegistry registry, IMessageBus bus, Command newGroup, RepoNodeFactory nodes)
    {
        _group = group;
        _registry = registry;
        HeaderVm = new GroupHeaderRowViewModel(group, registry, bus, newGroup);
        _primaryRepos = new Derived<IReadOnlyList<Repo>>(ComputeVisiblePrimaries);
        _primaries = new KeyedViewModelList<Repo, Guid, RepoNodeViewModel>(
            _primaryRepos, r => r.Id, r => nodes.Create(r, 0));
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
        _primaries.Dispose();
        _primaryRepos.Dispose();
        HeaderVm.Dispose();
    }
}
