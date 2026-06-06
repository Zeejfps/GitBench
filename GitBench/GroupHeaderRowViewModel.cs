using ZGF.Observable;

namespace GitBench;

internal sealed class GroupHeaderRowViewModel : IDisposable
{
    private readonly Group _group;
    private readonly IRepoRegistry _registry;
    private readonly Derived<bool> _isRenaming;

    public Group Group => _group;
    public IReadable<bool> IsRenaming => _isRenaming;
    public bool CanDelete => _registry.Groups.Count > 1;

    public Command ToggleCollapsed { get; }
    public Command BeginRename { get; }
    public Command Delete { get; }
    public Command NewGroup { get; }
    public Command ExpandAllGroups { get; }
    public Command CollapseAllGroups { get; }

    // Only meaningful to offer "Collapse/Expand All" when there's more than one group;
    // with a single group the header's own chevron already does the job.
    public bool HasMultipleGroups => _registry.Groups.Count > 1;

    public GroupHeaderRowViewModel(Group group, IRepoRegistry registry, Command newGroup)
    {
        _group = group;
        _registry = registry;
        NewGroup = newGroup;
        _isRenaming = new Derived<bool>(() => _registry.RenamingGroupId.Value == _group.Id);

        ToggleCollapsed = new Command(() => _registry.ToggleGroupCollapsed(_group.Id));
        BeginRename = new Command(() => _registry.BeginRenameGroup(_group.Id));
        Delete = new Command(() => _registry.DeleteGroup(_group.Id));
        ExpandAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(false));
        CollapseAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(true));
    }

    public void Dispose() => _isRenaming.Dispose();
}
