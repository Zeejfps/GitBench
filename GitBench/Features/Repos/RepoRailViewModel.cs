using GitBench.Git;
using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Backs the collapsed repo rail: one section per group, each listing the group's primary repos
/// as tiles. Ignores group collapse — the rail has no group headers to expand from, so every
/// primary stays reachable while the bar is collapsed.
/// </summary>
internal sealed class RepoRailViewModel : IDisposable
{
    private readonly IDisposable _sectionsSubscription;

    public ObservableList<RailSectionViewModel> Sections { get; }

    public RepoRailViewModel(IRepoRegistry registry, RepoNodeFactory nodes)
    {
        Sections = registry.Groups.Map(
            g => new RailSectionViewModel(g, registry, nodes),
            out _sectionsSubscription,
            vm => vm.Dispose());
    }

    public void Dispose() => _sectionsSubscription.Dispose();
}

internal sealed class RailSectionViewModel : IDisposable
{
    private readonly Derived<IReadOnlyList<Repo>> _primaryRepos;
    private readonly KeyedViewModelList<Repo, Guid, RepoNodeViewModel> _primaries;
    private readonly Derived<bool> _isFirst;

    public ObservableList<RepoNodeViewModel> Primaries => _primaries.Items;

    // The rail draws a divider above every section but the leading one, standing in for the
    // group headers the rail has no room for.
    public IReadable<bool> IsFirst => _isFirst;

    public RailSectionViewModel(Group group, IRepoRegistry registry, RepoNodeFactory nodes)
    {
        _primaryRepos = new Derived<IReadOnlyList<Repo>>(() =>
        {
            var reposById = registry.Repos.ToDictionary(r => r.Id);
            var result = new List<Repo>();
            foreach (var repoId in group.RepoIds)
            {
                if (reposById.TryGetValue(repoId, out var repo) && repo.IsPrimary)
                    result.Add(repo);
            }
            return result;
        });
        _primaries = new KeyedViewModelList<Repo, Guid, RepoNodeViewModel>(
            _primaryRepos, r => r.Id, r => nodes.Create(r, 0));
        _isFirst = new Derived<bool>(() => registry.Groups.Count > 0 && registry.Groups[0].Id == group.Id);
    }

    public void Dispose()
    {
        _isFirst.Dispose();
        _primaries.Dispose();
        _primaryRepos.Dispose();
    }
}
