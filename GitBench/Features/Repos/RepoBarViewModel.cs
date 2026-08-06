using GitBench.Controls;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed class RepoBarViewModel : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IDisposable _groupSectionsSubscription;
    private readonly SpinnerAnimation _loadSpinner;
    private readonly IDisposable _loadSubscription;

    public ObservableList<GroupSectionViewModel> GroupSections { get; }
    public Command NewGroup { get; }
    public Command ExpandAllGroups { get; }
    public Command CollapseAllGroups { get; }

    public bool HasMultipleGroups => _registry.Groups.Count > 1;

    // The angle every loading row's spinner turns at. One animation for the whole bar, so rows turn
    // in phase and nothing ticks while the bar is idle.
    public IReadable<float> LoadRotation => _loadSpinner.Rotation;

    public RepoBarViewModel(IRepoRegistry registry, IMessageBus bus, RepoNodeFactory nodes, IRepoLoadStore load, IFrameTicker ticker)
    {
        _registry = registry;
        NewGroup = new Command(DoNewGroup);
        ExpandAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(false));
        CollapseAllGroups = new Command(() => _registry.SetAllGroupsCollapsed(true));
        GroupSections = _registry.Groups.Map(
            g => new GroupSectionViewModel(g, registry, bus, NewGroup, nodes),
            out _groupSectionsSubscription,
            vm => vm.Dispose());

        _loadSpinner = new SpinnerAnimation(ticker);
        // Subscribing fires immediately, so a bar built mid-load starts already turning.
        _loadSubscription = load.AnyLoading.Subscribe(any =>
        {
            if (any) _loadSpinner.Start();
            else _loadSpinner.Stop();
        });
    }

    private void DoNewGroup()
    {
        var id = _registry.CreateGroup("New Group");
        _registry.BeginRenameGroup(id);
    }

    public void Dispose()
    {
        _loadSubscription.Dispose();
        _loadSpinner.Dispose();
        _groupSectionsSubscription.Dispose();
    }
}
