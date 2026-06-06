using ZGF.Observable;

namespace GitBench;

internal sealed class AbortOperationDialogViewModel : IDisposable
{
    private readonly State<bool> _forceQuitMode = new(false);
    // Plain field, not a State<T>: written in the background work lambda and read back in the
    // UI-thread onError callback (which runs after work completes). It drives no binding — only
    // _forceQuitMode does — so it needs no notifications, and a plain assignment avoids firing
    // them off the worker thread.
    private bool _forceQuitAvailable;

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
                _forceQuitAvailable = outcome.ForceQuitAvailable;
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
                if (_forceQuitAvailable && !_forceQuitMode.Value)
                    _forceQuitMode.Value = true;
            });
    }

    public void Dispose() { }

    // Single source of truth for the confirm-button label. The dialog seeds its button from
    // this too, so the label map isn't duplicated across the view and view model.
    internal static string DefaultConfirmLabel(RepoOperationState state) => state switch
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
