using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Operations;

internal sealed class AbortOperationDialogViewModel : IDialogViewModel
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
        IMessageBus bus,
        ILocalizationService loc)
    {
        var s = loc.Strings.Value;
        var defaultLabel = DefaultConfirmLabel(s, request.State);
        ConfirmButtonLabel = new Derived<string>(() => _forceQuitMode.Value ? s.OperationsAbortForceClear : defaultLabel);

        var repoId = request.Repo.Id;
        var gate = new Derived<bool>(() => request.State != RepoOperationState.None);

        Abort = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.AbortOperation(request.Repo, request.State, _forceQuitMode.Value);
                if (outcome is AbortOutcome.Failed failed)
                {
                    _forceQuitAvailable = failed.ForceQuitAvailable;
                    return failed.Message;
                }
                _forceQuitAvailable = false;
                return null;
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
    internal static string DefaultConfirmLabel(Strings s, RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => s.OperationsAbortMerge,
        RepoOperationState.Rebase => s.OperationsAbortRebase,
        RepoOperationState.CherryPick => s.OperationsAbortCherryPick,
        RepoOperationState.Revert => s.OperationsAbortRevert,
        RepoOperationState.ApplyMailbox => s.OperationsAbortApply,
        RepoOperationState.Bisect => s.OperationsAbortBisect,
        RepoOperationState.UnmergedPaths => s.OperationsAbortUnmerged,
        _ => s.CommonAbort,
    };
}

internal readonly record struct AbortOperationRequest(Repo Repo, RepoOperationState State);
