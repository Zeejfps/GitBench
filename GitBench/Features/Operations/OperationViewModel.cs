using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Shared model for an in-progress repo operation (merge / rebase / cherry-pick / revert / am /
/// bisect / unmerged paths). Drives the status banner, the bottom action panel, and whether the
/// commit box is shown. The operation is a typed <see cref="RepoOperation"/> so each surface reads
/// exactly the data its variant carries — progress only where progress exists, etc.
/// </summary>
internal sealed class OperationViewModel : ViewModelBase<OperationVmState>
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;
    private readonly SpinnerAnimation _spinner;
    // Exclusive lane for `git X --continue/--skip`. Off the default Gen lane so a Reload landing
    // mid-advance can't drop the advance's result.
    private readonly GenerationGuard _continueLane;

    public IReadable<RepoOperation?> Operation { get; }
    public IReadable<bool> IsActive { get; }
    public IReadable<bool> IsSequencer { get; }
    public IReadable<bool> HasSubject { get; }
    public IReadable<string?> Subject { get; }
    public IReadable<bool> HasProgress { get; }
    public IReadable<int> ProgressStep { get; }
    public IReadable<int> ProgressTotal { get; }
    public IReadable<float> ProgressFraction { get; }
    public IReadable<string?> Context { get; }
    public IReadable<bool> HasContext { get; }
    public IReadable<bool> ShowsConflictCue { get; }
    public IReadable<bool> IsConflicted { get; }
    public IReadable<int> ConflictCount { get; }
    public IReadable<string?> ConflictCountLabel { get; }
    public IReadable<bool> ShowsCommitBox { get; }
    public IReadable<bool> IsBusy => _spinner.IsActive;
    public IReadable<float> BusyRotation => _spinner.Rotation;
    public Command Abort { get; }
    public Command Continue { get; }
    public Command Skip { get; }

    public OperationViewModel(
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus,
        ILocalizationService loc)
        : base(dispatcher, OperationVmState.Initial)
    {
        _registry = registry;
        _gitService = gitService;
        _bus = bus;
        _loc = loc;
        _spinner = new SpinnerAnimation(ticker);
        _continueLane = CreateLane();

        IsActive = Slice(s => s.Operation is not null);
        Operation = Slice(s => s.Operation);
        IsSequencer = Slice(s => s.Operation is ISequencerOperation);
        Subject = Slice(s => s.Operation?.StoppedSubject());
        HasSubject = Slice(s => !string.IsNullOrEmpty(s.Operation?.StoppedSubject()));
        HasProgress = Slice(s => s.Operation is IProgressOperation { Total: > 0 });
        ProgressStep = Slice(s => s.Operation is IProgressOperation { Total: > 0 } p ? p.Step : 0);
        ProgressTotal = Slice(s => s.Operation is IProgressOperation { Total: > 0 } p ? p.Total : 0);
        ProgressFraction = Slice(s => s.Operation is IProgressOperation { Total: > 0 } p ? Math.Clamp((float)p.Step / p.Total, 0f, 1f) : 0f);
        Context = Slice(s => s.Operation is RebaseOperation r ? FormatContext(r) : null);
        HasContext = Slice(s => s.Operation is RebaseOperation r && FormatContext(r) is not null);
        ShowsConflictCue = Slice(s => s.Operation is IConflictableOperation);
        IsConflicted = Slice(s => s.Operation is IConflictableOperation { ConflictCount: > 0 });
        ConflictCount = Slice(s => (s.Operation as IConflictableOperation)?.ConflictCount ?? 0);
        ConflictCountLabel = Slice(s => s.Operation is IConflictableOperation { ConflictCount: > 0 } c ? c.ConflictCount.ToString() : null);
        ShowsCommitBox = Slice(s => s.Operation.ShowsCommitBox());

        Abort = new Command(DoAbort, Slice(s => s.Operation is not null));
        Continue = new Command(DoContinue, Slice(s => s.Operation is { } op && op.CanContinue()));
        Skip = new Command(DoSkip, Slice(s => s.Operation is { } op && op.CanSkip()));

        Subscriptions.Add(_registry.Active.Subscribe(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<RefsChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<WorkingTreeChangedMessage>(_ => Reload()));
        Subscriptions.Add(_bus.SubscribeScoped<CommitCreatedMessage>(_ => Reload()));
    }

    private void DoAbort()
    {
        var repo = _registry.Active.Value;
        var op = Operation.Value;
        if (repo == null || op == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new AbortOperationDialog { Repo = repo, State = op.Kind, OnClose = onClose }));
    }

    private void DoContinue() => Advance(skip: false);

    private void DoSkip() => Advance(skip: true);

    private void Advance(bool skip)
    {
        var repo = _registry.Active.Value;
        var op = Operation.Value;
        if (repo == null || op == null) return;

        var service = _gitService;
        var bus = _bus;
        var started = TryRunOutcome(
            _continueLane,
            () => skip ? service.SkipOperation(repo, op.Kind) : service.ContinueOperation(repo, op.Kind),
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
                        Update(_ => OperationVmState.Initial);
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
            Update(_ => OperationVmState.Initial);
            return;
        }

        var repoId = repo.Id;
        var service = _gitService;
        RunBackground<RepoOperation?>(
            () =>
            {
                try { return (service.GetOperation(repo), null); }
                catch { return (null, null); }
            },
            (op, _) =>
            {
                if (_registry.Active.Value?.Id != repoId) return;
                Update(_ => new OperationVmState(op));
            });
    }

    private static string? FormatContext(RebaseOperation r)
    {
        var src = string.IsNullOrEmpty(r.SourceLabel) ? null : r.SourceLabel;
        var onto = string.IsNullOrEmpty(r.OntoLabel) ? null : r.OntoLabel;
        if (src != null && onto != null) return $"{src} → {onto}";
        if (onto != null) return $"→ {onto}";
        return src;
    }

    public override void Dispose()
    {
        _spinner.Dispose();
        base.Dispose();
    }
}

internal sealed record OperationVmState(RepoOperation? Operation)
{
    public static OperationVmState Initial { get; } = new((RepoOperation?)null);
}
