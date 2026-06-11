using GitBench.Controls;
using GitBench.Features.Branches;
using GitBench.Features.Operations;
using GitBench.Features.Repos;
using GitBench.Features.Stash;
using GitBench.Infrastructure;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Toolbar;

internal sealed class ActionsToolbarViewModel : ViewModelBase<ActionsToolbarState>
{
    private readonly IRepoRegistry _registry;
    private readonly IPlatformShell _shell;
    private readonly IMessageBus _bus;
    private readonly IRepoOperationsStore _ops;

    private readonly SpinnerAnimation _pushSpinner;
    private readonly SpinnerAnimation _pullSpinner;
    private readonly SpinnerAnimation _fetchSpinner;

    public Command Push { get; }
    public Command Pull { get; }
    public Command Fetch { get; }
    public Command Branch { get; }
    public Command Stash { get; }
    public Command OpenFolder { get; }
    public Command OpenTerminal { get; }

    public IReadable<int?> PushBadge { get; }
    public IReadable<int?> PullBadge { get; }
    public IReadable<bool> IsPushing { get; }
    public IReadable<bool> IsPulling { get; }
    public IReadable<bool> IsFetching { get; }

    public IReadable<float> PushRotation => _pushSpinner.Rotation;
    public IReadable<float> PullRotation => _pullSpinner.Rotation;
    public IReadable<float> FetchRotation => _fetchSpinner.Rotation;

    public ActionsToolbarViewModel(
        IRepoRegistry registry,
        IPlatformShell shell,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus,
        IRepoStatusStore status,
        IRepoOperationsStore ops)
        : base(dispatcher, ActionsToolbarState.Initial)
    {
        _registry = registry;
        _shell = shell;
        _bus = bus;
        _ops = ops;

        _pushSpinner = new SpinnerAnimation(ticker);
        _pullSpinner = new SpinnerAnimation(ticker);
        _fetchSpinner = new SpinnerAnimation(ticker);

        var repoActionsEnabled = Slice(s => s.HasActiveRepo);
        Push = new Command(DoPush, Slice(ComputePushEnabled));
        Pull = new Command(DoPull, Slice(ComputePullEnabled));
        Fetch = new Command(DoFetch, Slice(s => !s.IsFetching && s.HasActiveRepo));
        Branch = new Command(DoBranch, repoActionsEnabled);
        Stash = new Command(DoStash, Slice(s => s.HasActiveRepo && s.Status.IsDirty));
        OpenFolder = new Command(DoOpenFolder, repoActionsEnabled);
        OpenTerminal = new Command(DoOpenTerminal, repoActionsEnabled);

        PushBadge = Slice(ComputePushBadge);
        PullBadge = Slice(ComputePullBadge);
        IsPushing = Slice(s => s.IsPushing);
        IsPulling = Slice(s => s.IsPulling);
        IsFetching = Slice(s => s.IsFetching);

        // HasActiveRepo gates the command-enabled slices; the registry drives it directly. The
        // cheap branch/ahead/behind/dirty signals are projected from the status store.
        Subscriptions.Add(_registry.Active.Subscribe(repo =>
            Update(s => s with { HasActiveRepo = repo != null })));
        Subscriptions.Add(status.Active.Subscribe(st =>
            Update(s => s with { Status = st })));

        // Push/pull/fetch in-flight state is owned per-repo by the operations store. Project the
        // active repo's slice into our flags + spinners, so switching repos shows the right state
        // and a background op keeps tracking after the switch.
        Subscriptions.Add(_ops.Active.Subscribe(OnOps));

        // A diverged pull on the active repo is recoverable — open the reconcile dialog.
        Subscriptions.Add(_bus.SubscribeScoped<PullDivergedMessage>(m =>
        {
            if (_registry.Active.Value?.Id == m.Repo.Id)
                _bus.Broadcast(new ShowDialogMessage(onClose => new ReconcilePullDialog
                {
                    Repo = m.Repo,
                    OnClose = onClose,
                }));
        }));
    }

    private void OnOps(RepoOperations ops)
    {
        Update(s => s with
        {
            IsPushing = ops.IsPushing,
            IsPulling = ops.IsPulling,
            IsFetching = ops.IsFetching,
        });
        DriveSpinner(_pushSpinner, ops.IsPushing);
        DriveSpinner(_pullSpinner, ops.IsPulling);
        DriveSpinner(_fetchSpinner, ops.IsFetching);
    }

    private static void DriveSpinner(SpinnerAnimation spinner, bool active)
    {
        if (active) spinner.Start();
        else spinner.Stop();
    }

    private static bool ComputePushEnabled(ActionsToolbarState s)
    {
        var hasBranchUpstream = !s.Status.IsDetached && s.Status.HasUpstream;
        var canPublish = !s.Status.IsDetached && !s.Status.HasUpstream
            && !string.IsNullOrEmpty(s.Status.CurrentBranchName);
        return !s.IsPushing && ((hasBranchUpstream && s.Status.Ahead > 0) || canPublish);
    }

    private static bool ComputePullEnabled(ActionsToolbarState s)
    {
        var hasBranchUpstream = !s.Status.IsDetached && s.Status.HasUpstream;
        return !s.IsPulling && hasBranchUpstream && s.Status.Behind > 0;
    }

    private static int? ComputePushBadge(ActionsToolbarState s)
    {
        if (s.IsPushing) return null;
        var hasBranchUpstream = !s.Status.IsDetached && s.Status.HasUpstream;
        return hasBranchUpstream ? s.Status.Ahead : 0;
    }

    private static int? ComputePullBadge(ActionsToolbarState s)
    {
        if (s.IsPulling) return null;
        var hasBranchUpstream = !s.Status.IsDetached && s.Status.HasUpstream;
        return hasBranchUpstream ? s.Status.Behind : 0;
    }

    private void DoOpenFolder()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        try { _shell.OpenFolder(repo.Path); }
        catch (Exception ex) { _bus.Broadcast(new ShowOperationErrorMessage("Open folder failed", ex.Message)); }
    }

    private void DoOpenTerminal()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        try { _shell.OpenTerminal(repo.Path); }
        catch (Exception ex) { _bus.Broadcast(new ShowOperationErrorMessage("Open terminal failed", ex.Message)); }
    }

    private void DoBranch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var status = State.Value.Status;
        // Detached HEAD has no branch name to seed from; "HEAD" still works as a starting
        // ref for `git branch newname HEAD` and matches Fork's default.
        var suggested = status.IsDetached || string.IsNullOrEmpty(status.CurrentBranchName)
            ? "HEAD"
            : status.CurrentBranchName;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog
        {
            Repo = repo,
            SuggestedStartPoint = suggested,
            OnClose = onClose,
        }));
    }

    private void DoStash()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new StashDialog
        {
            Repo = repo,
            OnClose = onClose,
        }));
    }

    private void DoPush()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var status = State.Value.Status;

        if (!status.IsDetached
            && !status.HasUpstream
            && !string.IsNullOrEmpty(status.CurrentBranchName))
        {
            var localBranch = status.CurrentBranchName!;
            _bus.Broadcast(new ShowDialogMessage(onClose => new PublishBranchDialog
            {
                Repo = repo,
                LocalBranch = localBranch,
                OnClose = onClose,
            }));
            return;
        }

        if (!status.IsDetached
            && status.HasUpstream
            && status.Ahead > 0
            && status.Behind > 0)
        {
            var branchName = status.CurrentBranchName ?? string.Empty;
            _bus.Broadcast(new ShowDialogMessage(onClose => new ForcePushDialog
            {
                Repo = repo,
                BranchName = branchName,
                Ahead = status.Ahead,
                Behind = status.Behind,
                OnClose = onClose,
            }));
            return;
        }

        _ops.Push(repo);
    }

    private void DoPull()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _ops.Pull(repo);
    }

    private void DoFetch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _ops.Fetch(repo);
    }

    public override void Dispose()
    {
        _pushSpinner.Dispose();
        _pullSpinner.Dispose();
        _fetchSpinner.Dispose();
        base.Dispose();
    }
}
