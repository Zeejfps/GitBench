using ZGF.Observable;

namespace GitBench;

// Drives the repo-level "detached HEAD with commits on no branch" banner. Mirrors
// OperationStateBannerViewModel: self-contained, always subscribed (independent of which
// main-content tab is showing), and recomputes its git state on the same change signals.
internal sealed class DetachedHeadBannerViewModel : ViewModelBase<DetachedHeadBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;

    public IReadable<bool> IsAtRisk { get; }
    public Command CreateBranch { get; }

    public DetachedHeadBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, DetachedHeadBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;

        IsAtRisk = Slice(s => s.IsAtRisk);
        CreateBranch = new Command(DoCreateBranch);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    // Seeds the CreateBranchDialog with "HEAD" so the branch is created at the detached
    // commit; the dialog's "checkout after create" box defaults on, so the common flow
    // captures the commits onto a branch and lands the user on it (clearing this banner).
    private void DoCreateBranch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog(repo, "HEAD", onClose)));
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            Update(_ => DetachedHeadBannerState.Initial);
            return;
        }

        var repoId = repo.Id;
        var service = _gitService;
        RunBackground<bool>(
            () =>
            {
                try { return (service.IsHeadDetachedAtRisk(repo), null); }
                catch { return (false, null); }
            },
            (atRisk, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new DetachedHeadBannerState(atRisk));
            });
    }
}

internal sealed record DetachedHeadBannerState(bool IsAtRisk)
{
    public static DetachedHeadBannerState Initial { get; } = new(false);
}
