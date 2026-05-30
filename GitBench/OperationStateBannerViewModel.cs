using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

internal sealed class OperationStateBannerViewModel : ViewModelBase<OperationBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly SpinnerAnimation _spinner;
    private bool _isContinuing;

    public IReadable<RepoOperationState> State { get; }
    public IReadable<bool> IsBusy => _spinner.IsActive;
    public IReadable<float> BusyRotation => _spinner.Rotation;
    public Command Abort { get; }
    public Command Continue { get; }

    public OperationStateBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
        : base(dispatcher, OperationBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _spinner = new SpinnerAnimation(dispatcher);

        State = Slice(s => s.State);
        Abort = new Command(DoAbort);
        Continue = new Command(DoContinue);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    private void DoAbort()
    {
        var repo = _registry.Active.Value;
        var state = State.Value;
        if (repo == null || state == RepoOperationState.None) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new AbortOperationDialog(repo, state, onClose)));
    }

    private void DoContinue()
    {
        if (_isContinuing) return;
        var repo = _registry.Active.Value;
        var state = State.Value;
        if (repo == null || state == RepoOperationState.None) return;

        _isContinuing = true;
        _spinner.Start();

        var service = _gitService;
        var bus = _bus;
        RunBackground<ContinueOperationOutcome>(
            () =>
            {
                try { return (service.ContinueOperation(repo, state), null); }
                catch (Exception ex) { return (new ContinueOperationOutcome(false, ex.Message), null); }
            },
            (outcome, _) =>
            {
                _isContinuing = false;
                _spinner.Stop();
                if (outcome!.Success)
                {
                    bus.Broadcast(new RefsChangedMessage(repo.Id));
                    bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                    return;
                }
                var title = outcome.HasMoreConflicts
                    ? "Resolve remaining conflicts"
                    : "Continue failed";
                bus.Broadcast(new ShowOperationErrorMessage(title, outcome.ErrorMessage ?? "Continue failed."));
            });
    }

    private void Reload()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            Update(_ => OperationBannerState.Initial);
            return;
        }

        var repoId = repo.Id;
        var service = _gitService;
        RunBackground<RepoOperationState>(
            () =>
            {
                try { return (service.GetOperationState(repo), null); }
                catch { return (RepoOperationState.None, null); }
            },
            (state, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new OperationBannerState(state));
            });
    }

    public override void Dispose()
    {
        _spinner.Dispose();
        base.Dispose();
    }
}

internal sealed record OperationBannerState(RepoOperationState State)
{
    public static OperationBannerState Initial { get; } = new(RepoOperationState.None);
}
