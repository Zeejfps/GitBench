using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Operations;

internal sealed class OperationStateBannerViewModel : ViewModelBase<OperationBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly SpinnerAnimation _spinner;
    // Exclusive lane for `git X --continue`. Deliberately off the default Gen lane: Reload()
    // runs there, and a reload landing mid-continue must not drop the continue's result.
    private readonly GenerationGuard _continueLane;

    public IReadable<RepoOperationState> State { get; }
    public IReadable<bool> IsBusy => _spinner.IsActive;
    public IReadable<float> BusyRotation => _spinner.Rotation;
    public Command Abort { get; }
    public Command Continue { get; }

    public OperationStateBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus)
        : base(dispatcher, OperationBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _spinner = new SpinnerAnimation(ticker);
        _continueLane = CreateLane();

        State = Slice(s => s.State);
        Abort = new Command(DoAbort);
        // Continue stays disabled while unmerged paths remain — git would refuse anyway.
        var canContinue = Slice(s => s.State != RepoOperationState.None && !s.HasConflicts);
        Continue = new Command(DoContinue, canContinue);

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
        var repo = _registry.Active.Value;
        var state = State.Value;
        if (repo == null || state == RepoOperationState.None) return;

        var service = _gitService;
        var bus = _bus;
        var started = TryRunOutcome(
            _continueLane,
            () => service.ContinueOperation(repo, state),
            outcome =>
            {
                _spinner.Stop();
                switch (outcome)
                {
                    case ContinueOutcome.MoreConflicts more:
                        bus.Broadcast(new ShowOperationErrorMessage("Resolve remaining conflicts", more.Message));
                        break;
                    case ContinueOutcome.Failed failed:
                        bus.Broadcast(new ShowOperationErrorMessage("Continue failed", failed.Message));
                        break;
                    default:
                        bus.Broadcast(new RefsChangedMessage(repo.Id));
                        bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
                        break;
                }
            });
        if (started) _spinner.Start();
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
        RunBackground<(RepoOperationState State, bool HasConflicts)>(
            () =>
            {
                try
                {
                    var state = service.GetOperationState(repo);
                    var hasConflicts = state != RepoOperationState.None && service.HasUnmergedPaths(repo);
                    return ((state, hasConflicts), null);
                }
                catch { return ((RepoOperationState.None, false), null); }
            },
            (result, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new OperationBannerState(result.State, result.HasConflicts));
            });
    }

    public override void Dispose()
    {
        _spinner.Dispose();
        base.Dispose();
    }
}

internal sealed record OperationBannerState(RepoOperationState State, bool HasConflicts = false)
{
    public static OperationBannerState Initial { get; } = new(RepoOperationState.None);
}
