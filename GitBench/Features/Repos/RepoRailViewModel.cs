using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Backs the collapsed repo rail: one folder per group, each listing the group's primary repos
/// as tiles while the group is expanded and a peek at them while it is collapsed. Shares the
/// group's collapse flag with the full bar, so folding a folder here folds the section there.
/// </summary>
internal sealed class RepoRailViewModel : IDisposable
{
    private readonly IDisposable _sectionsSubscription;

    public ObservableList<RailSectionViewModel> Sections { get; }
    public Command NewGroup { get; }

    public RepoRailViewModel(IRepoRegistry registry, IMessageBus bus, RepoNodeFactory nodes, RepoBarCollapseState collapse)
    {
        NewGroup = new Command(() =>
        {
            collapse.Expand();
            registry.BeginRenameGroup(registry.CreateGroup("New Group"));
        });
        Sections = registry.Groups.Map(
            g => new RailSectionViewModel(g, registry, bus, NewGroup, nodes, collapse.Expand),
            out _sectionsSubscription,
            vm => vm.Dispose());
    }

    public void Dispose() => _sectionsSubscription.Dispose();
}

internal sealed class RailSectionViewModel : IDisposable
{
    private readonly Derived<IReadOnlyList<Repo>> _primaryRepos;
    private readonly KeyedViewModelList<Repo, Guid, RepoNodeViewModel> _primaries;
    private readonly Derived<bool> _isExpanded;
    private readonly Derived<bool> _containsActive;
    private readonly Derived<RepoRowBadge> _badge;

    public Group Group { get; }
    public GroupHeaderRowViewModel HeaderVm { get; }
    public ObservableList<RepoNodeViewModel> Primaries => _primaries.Items;
    public IReadable<bool> IsExpanded => _isExpanded;

    public IReadable<bool> ContainsActive => _containsActive;

    public IReadable<RepoRowBadge> Badge => _badge;

    public ICommand ToggleCollapsed => HeaderVm.ToggleCollapsed;

    public RailSectionViewModel(
        Group group, IRepoRegistry registry, IMessageBus bus, Command newGroup, RepoNodeFactory nodes, Action beforeRename)
    {
        Group = group;
        HeaderVm = new GroupHeaderRowViewModel(group, registry, bus, newGroup, beforeRename);
        _primaryRepos = new Derived<IReadOnlyList<Repo>>(() => registry.PrimariesIn(group));
        _primaries = new KeyedViewModelList<Repo, Guid, RepoNodeViewModel>(
            _primaryRepos, r => r.Id, r => nodes.Create(r, 0));
        _isExpanded = new Derived<bool>(() => !group.IsCollapsed.Value);
        _containsActive = new Derived<bool>(() =>
        {
            var active = registry.Active.Value;
            if (active is null) return false;
            return group.RepoIds.Contains(active.PrimaryId);
        });
        _badge = new Derived<RepoRowBadge>(() =>
        {
            var worst = RepoRowBadge.None;
            foreach (var primary in Primaries)
            {
                var badge = primary.Badge.Value;
                if (badge > worst) worst = badge;
            }
            return worst;
        });
    }

    public void Dispose()
    {
        _badge.Dispose();
        _containsActive.Dispose();
        _isExpanded.Dispose();
        _primaries.Dispose();
        _primaryRepos.Dispose();
        HeaderVm.Dispose();
    }
}
