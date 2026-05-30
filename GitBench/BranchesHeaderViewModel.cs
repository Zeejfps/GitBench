using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

internal sealed class BranchesHeaderViewModel : ViewModelBase<BranchesHeaderState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<string?> BranchName { get; }
    public IReadable<bool> IsDetached { get; }

    public BranchesHeaderViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, BranchesHeaderState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        BranchName = Slice(s => s.BranchName);
        IsDetached = Slice(s => s.IsDetached);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            Update(_ => BranchesHeaderState.Initial);
            return;
        }

        var repoId = repo.Id;
        RunBackground<PushStatus>(
            () =>
            {
                try { return (_gitService.GetPushStatus(repo), null); }
                catch { return (new PushStatus(null, false, 0, 0, false), null); }
            },
            (status, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => status!.IsDetached
                    ? new BranchesHeaderState("(detached HEAD)", true)
                    : new BranchesHeaderState(status.CurrentBranchName, false));
            });
    }
}

internal sealed record BranchesHeaderState(string? BranchName, bool IsDetached)
{
    public static BranchesHeaderState Initial { get; } = new(null, false);
}
