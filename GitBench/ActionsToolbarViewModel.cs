using ZGF.Gui;
using ZGF.Observable;

namespace GitBench;

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
    public IReadable<string?> Error { get; }

    public IReadable<float> PushRotation => _pushSpinner.Rotation;
    public IReadable<float> PullRotation => _pullSpinner.Rotation;
    public IReadable<float> FetchRotation => _fetchSpinner.Rotation;

    public ActionsToolbarViewModel(
        IRepoRegistry registry,
        IPlatformShell shell,
        IUiDispatcher dispatcher,
        IFrameTicker ticker,
        IMessageBus bus,
        IRepoSnapshotStore store,
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
        Stash = new Command(DoStash, Slice(s => s.HasActiveRepo && s.HasLocalChanges));
        OpenFolder = new Command(DoOpenFolder, repoActionsEnabled);
        OpenTerminal = new Command(DoOpenTerminal, repoActionsEnabled);

        PushBadge = Slice(ComputePushBadge);
        PullBadge = Slice(ComputePullBadge);
        IsPushing = Slice(s => s.IsPushing);
        IsPulling = Slice(s => s.IsPulling);
        IsFetching = Slice(s => s.IsFetching);
        Error = Slice(s => s.ShellError ?? s.OpError);

        // HasActiveRepo gates the command-enabled slices; the registry drives it directly. Push
        // status and has-local-changes are projected from the snapshot store (no own loads/caches).
        Subscriptions.Add(_registry.Active.Subscribe(repo =>
            Update(s => s with { HasActiveRepo = repo != null, ShellError = null })));
        Subscriptions.Add(store.PushStatus.Subscribe(status =>
            Update(s => s with { PushStatus = status })));
        Subscriptions.Add(store.LocalChanges.Subscribe(data =>
        {
            var hasChanges = data != null && data.Snapshot.Staged.Count + data.Snapshot.Unstaged.Count > 0;
            Update(s => s.HasLocalChanges == hasChanges ? s : s with { HasLocalChanges = hasChanges });
        }));

        // Push/pull/fetch in-flight state is owned per-repo by the operations store. Project the
        // active repo's slice into our flags + spinners + inline error, so switching repos shows the
        // right state and a background op keeps tracking after the switch.
        Subscriptions.Add(_ops.Active.Subscribe(OnOps));

        // A diverged pull on the active repo is recoverable — open the reconcile dialog.
        Subscriptions.Add(_bus.SubscribeScoped<PullDivergedMessage>(m =>
        {
            if (_registry.Active.Value?.Id == m.Repo.Id)
                _bus.Broadcast(new ShowDialogMessage(onClose => new ReconcilePullDialog(m.Repo, onClose)));
        }));
    }

    private void OnOps(RepoOperations ops)
    {
        Update(s => s with
        {
            IsPushing = ops.IsPushing,
            IsPulling = ops.IsPulling,
            IsFetching = ops.IsFetching,
            OpError = ops.LastError,
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
        var hasBranchUpstream = !s.PushStatus.IsDetached && s.PushStatus.HasUpstream;
        var canPublish = !s.PushStatus.IsDetached && !s.PushStatus.HasUpstream
            && !string.IsNullOrEmpty(s.PushStatus.CurrentBranchName);
        return !s.IsPushing && ((hasBranchUpstream && s.PushStatus.Ahead > 0) || canPublish);
    }

    private static bool ComputePullEnabled(ActionsToolbarState s)
    {
        var hasBranchUpstream = !s.PushStatus.IsDetached && s.PushStatus.HasUpstream;
        return !s.IsPulling && hasBranchUpstream && s.PushStatus.Behind > 0;
    }

    private static int? ComputePushBadge(ActionsToolbarState s)
    {
        if (s.IsPushing) return null;
        var hasBranchUpstream = !s.PushStatus.IsDetached && s.PushStatus.HasUpstream;
        return hasBranchUpstream ? s.PushStatus.Ahead : 0;
    }

    private static int? ComputePullBadge(ActionsToolbarState s)
    {
        if (s.IsPulling) return null;
        var hasBranchUpstream = !s.PushStatus.IsDetached && s.PushStatus.HasUpstream;
        return hasBranchUpstream ? s.PushStatus.Behind : 0;
    }

    private void DoOpenFolder()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        try { _shell.OpenFolder(repo.Path); }
        catch (Exception ex) { Update(s => s with { ShellError = $"Open folder failed: {ex.Message}" }); }
    }

    private void DoOpenTerminal()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        try { _shell.OpenTerminal(repo.Path); }
        catch (Exception ex) { Update(s => s with { ShellError = $"Open terminal failed: {ex.Message}" }); }
    }

    private void DoBranch()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var pushStatus = State.Value.PushStatus;
        // Detached HEAD has no branch name to seed from; "HEAD" still works as a starting
        // ref for `git branch newname HEAD` and matches Fork's default.
        var suggested = pushStatus.IsDetached || string.IsNullOrEmpty(pushStatus.CurrentBranchName)
            ? "HEAD"
            : pushStatus.CurrentBranchName;
        _bus.Broadcast(new ShowDialogMessage(onClose => new CreateBranchDialog(repo, suggested, onClose)));
    }

    private void DoStash()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        _bus.Broadcast(new ShowDialogMessage(onClose => new StashDialog(repo, onClose)));
    }

    private void DoPush()
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var pushStatus = State.Value.PushStatus;

        if (!pushStatus.IsDetached
            && !pushStatus.HasUpstream
            && !string.IsNullOrEmpty(pushStatus.CurrentBranchName))
        {
            var localBranch = pushStatus.CurrentBranchName!;
            _bus.Broadcast(new ShowDialogMessage(onClose => new PublishBranchDialog(
                new PublishBranchRequest(repo, localBranch), onClose)));
            return;
        }

        if (!pushStatus.IsDetached
            && pushStatus.HasUpstream
            && pushStatus.Ahead > 0
            && pushStatus.Behind > 0)
        {
            var branchName = pushStatus.CurrentBranchName ?? string.Empty;
            _bus.Broadcast(new ShowDialogMessage(onClose => new ForcePushDialog(
                repo, branchName, pushStatus.Ahead, pushStatus.Behind, onClose)));
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
