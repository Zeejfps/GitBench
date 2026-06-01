using ZGF.Observable;

namespace GitGui;

internal sealed class RepoBarViewModel : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IDisposable _groupSectionsSubscription;

    public ObservableList<GroupSectionViewModel> GroupSections { get; }
    public Command NewGroup { get; }
    public Command ExpandAllGroups { get; }
    public Command CollapseAllGroups { get; }

    public bool HasMultipleGroups => _registry.Groups.Count > 1;

    public RepoBarViewModel(IRepoRegistry registry)
    {
        _registry = registry;
        NewGroup = new Command(DoNewGroup);
        ExpandAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(false));
        CollapseAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(true));
        GroupSections = _registry.Groups.Map(
            g => new GroupSectionViewModel(g, registry, NewGroup),
            out _groupSectionsSubscription,
            vm => vm.Dispose());
    }

    private void DoNewGroup()
    {
        var id = _registry.CreateGroup("New Group");
        _registry.BeginRenameGroup(id);
    }

    public void Dispose()
    {
        _groupSectionsSubscription.Dispose();
    }
}
