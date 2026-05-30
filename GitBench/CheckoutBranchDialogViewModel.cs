using ZGF.Observable;

namespace GitGui;

internal sealed class CheckoutBranchDialogViewModel : IDisposable
{
    public State<string> Name { get; }
    public State<bool> Track { get; } = new(true);
    public AsyncCommand Checkout { get; }

    public event Action? CloseRequested;

    public CheckoutBranchDialogViewModel(
        CheckoutRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Name = new State<string>(request.SuggestedLocalName);

        var repoId = request.Repo.Id;
        var gate = new Derived<bool>(() => Name.Value.Length > 0);

        Checkout = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.CheckoutRemoteBranch(
                    request.Repo, Name.Value, request.RemoteName, request.RemoteBranchName, Track.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Checkout failed.");
            },
            // Close before broadcasting: an error broadcast triggers OverlayView to swap in the
            // error dialog, and a stale Close() afterwards would dismiss that brand-new dialog
            // instead of this one. Both paths close, so the ordering holds either way.
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new RefsChangedMessage(repoId));
            },
            gate: gate,
            onError: error =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new ShowOperationErrorMessage("Checkout failed", error));
            });
    }

    public void Dispose() { }
}
