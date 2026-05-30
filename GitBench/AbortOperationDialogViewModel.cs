using ZGF.Observable;

namespace GitGui;

internal sealed class AbortOperationDialogViewModel : IDisposable
{
    private readonly State<bool> _forceQuitMode = new(false);
    private readonly State<bool> _forceQuitAvailable = new(false);

    public IReadable<string> ConfirmButtonLabel { get; }
    public IReadable<string?> Error => Abort.Error;

    public AsyncCommand Abort { get; }

    public event Action? CloseRequested;

    public AbortOperationDialogViewModel(
        AbortOperationRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var defaultLabel = DefaultConfirmLabel(request.State);
        ConfirmButtonLabel = new Derived<string>(() => _forceQuitMode.Value ? "Force clear" : defaultLabel);

        var repoId = request.Repo.Id;
        var gate = new Derived<bool>(() => request.State != RepoOperationState.None);

        Abort = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.AbortOperation(request.Repo, request.State, _forceQuitMode.Value);
                _forceQuitAvailable.Value = outcome.ForceQuitAvailable;
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Abort failed.");
            },
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new RefsChangedMessage(repoId));
                bus.Broadcast(new WorkingTreeChangedMessage(repoId));
            },
            gate: gate,
            onError: _ =>
            {
                // A first failure that reports force-quit availability flips the button into
                // "Force clear" mode so a second press can hard-clear the operation state.
                if (_forceQuitAvailable.Value && !_forceQuitMode.Value)
                    _forceQuitMode.Value = true;
            });
    }

    public void Dispose() { }

    private static string DefaultConfirmLabel(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => "Abort merge",
        RepoOperationState.Rebase => "Abort rebase",
        RepoOperationState.CherryPick => "Abort cherry-pick",
        RepoOperationState.Revert => "Abort revert",
        RepoOperationState.ApplyMailbox => "Abort apply",
        RepoOperationState.Bisect => "Reset bisect",
        RepoOperationState.UnmergedPaths => "Reset",
        _ => "Abort",
    };
}

internal readonly record struct AbortOperationRequest(Repo Repo, RepoOperationState State);
