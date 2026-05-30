using ZGF.Observable;

namespace GitGui;

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

    public GroupHeaderRowViewModel(Group group, IRepoRegistry registry, Command newGroup)
    {
        _group = group;
        _registry = registry;
        NewGroup = newGroup;
        _isRenaming = new Derived<bool>(() => _registry.RenamingGroupId.Value == _group.Id);

        ToggleCollapsed = new Command(() => _registry.ToggleGroupCollapsed(_group.Id));
        BeginRename = new Command(() => _registry.BeginRenameGroup(_group.Id));
        Delete = new Command(() => _registry.DeleteGroup(_group.Id));
    }

    public void Dispose() => _isRenaming.Dispose();
}
