using ZGF.Observable;

namespace GitGui;

internal sealed class DeleteRemoteBranchDialogViewModel : IDisposable
{
    public AsyncCommand Delete { get; }
    public event Action? CloseRequested;

    public DeleteRemoteBranchDialogViewModel(
        DeleteRemoteBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Delete = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.DeleteRemoteBranch(request.Repo, request.RemoteName, request.BranchName);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Delete failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}

public readonly record struct DeleteRemoteBranchRequest(Repo Repo, string RemoteName, string BranchName);
