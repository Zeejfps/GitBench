using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Operations;

internal sealed class OperationBannerViewModel : ViewModelBase<OperationBannerState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly SpinnerAnimation _spinner;
    // Exclusive lane for `git X --continue`. Deliberately off the default Gen lane: Reload()
    // runs there, and a reload landing mid-continue must not drop the continue's result.
    private readonly GenerationGuard _continueLane;

    public IReadable<bool> IsActive { get; }
    public IReadable<RepoOperationState> OperationState { get; }
    public IReadable<bool> HasConflicts { get; }
    public IReadable<string?> Subject { get; }
    public IReadable<bool> HasSubject { get; }
    public IReadable<bool> IsBusy => _spinner.IsActive;
    public IReadable<float> BusyRotation => _spinner.Rotation;
    public Command Abort { get; }
    public Command Continue { get; }
    public Command Skip { get; }

    public OperationBannerViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus,
        ILocalizationService loc)
        : base(dispatcher, OperationBannerState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _loc = loc;
        _spinner = new SpinnerAnimation(ticker);
        _continueLane = CreateLane();

        // Declared before State so the banner swaps out before any per-state content would
        // re-render when the operation clears.
        IsActive = Slice(s => s.State != RepoOperationState.None);
        OperationState = Slice(s => s.State);
        HasConflicts = Slice(s => s.HasConflicts);
        Subject = Slice(s => s.Subject);
        HasSubject = Slice(s => !string.IsNullOrEmpty(s.Subject));
        Abort = new Command(DoAbort);
        // Continue stays disabled while unmerged paths remain — git would refuse anyway.
        var canContinue = Slice(s => s.State != RepoOperationState.None && !s.HasConflicts);
        Continue = new Command(DoContinue, canContinue);
        var canSkip = Slice(s => SupportsSkip(s.State));
        Skip = new Command(DoSkip, canSkip);

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    private void DoAbort()
    {
        var repo = _registry.Active.Value;
        var state = OperationState.Value;
        if (repo == null || state == RepoOperationState.None) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new AbortOperationDialog { Repo = repo, State = state, OnClose = onClose }));
    }

    private void DoContinue() => Advance(skip: false);

    private void DoSkip() => Advance(skip: true);

    private void Advance(bool skip)
    {
        var repo = _registry.Active.Value;
        var state = OperationState.Value;
        if (repo == null || state == RepoOperationState.None) return;

        var service = _gitService;
        var bus = _bus;
        var started = TryRunOutcome(
            _continueLane,
            () => skip ? service.SkipOperation(repo, state) : service.ContinueOperation(repo, state),
            outcome =>
            {
                var strings = _loc.Strings.Value;
                switch (outcome)
                {
                    case ContinueOutcome.MoreConflicts more:
                        _spinner.Stop();
                        bus.Broadcast(new ShowOperationErrorMessage(strings.OperationsErrorResolveRemaining, more.Message));
                        break;
                    case ContinueOutcome.Failed failed:
                        _spinner.Stop();
                        bus.Broadcast(new ShowOperationErrorMessage(strings.OperationsErrorContinueFailed, failed.Message));
                        break;
                    default:
                        // Operation finished: clear the banner (IsActive -> false) before stopping the
                        // spinner so it unmounts in one step. Stopping the spinner first would flip the
                        // inner Show back to the action buttons for the frames until the async Reload
                        // lands and hides the banner — the flash. The Reload still runs and confirms None.
                        Update(_ => OperationBannerState.Initial);
                        _spinner.Stop();
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
        RunBackground<(RepoOperationState State, bool HasConflicts, string? Subject)>(
            () =>
            {
                try
                {
                    var state = service.GetOperationState(repo);
                    var hasConflicts = state != RepoOperationState.None && service.HasUnmergedPaths(repo);
                    var subject = state != RepoOperationState.None ? service.GetOperationCommitSubject(repo, state) : null;
                    return ((state, hasConflicts, subject), null);
                }
                catch { return ((RepoOperationState.None, false, (string?)null), null); }
            },
            (result, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new OperationBannerState(result.State, result.HasConflicts, result.Subject));
            });
    }

    private static bool SupportsSkip(RepoOperationState state) => state switch
    {
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        _ => false,
    };

    public override void Dispose()
    {
        _spinner.Dispose();
        base.Dispose();
    }
}

internal sealed record OperationBannerState(RepoOperationState State, bool HasConflicts = false, string? Subject = null)
{
    public static OperationBannerState Initial { get; } = new(RepoOperationState.None);
}
