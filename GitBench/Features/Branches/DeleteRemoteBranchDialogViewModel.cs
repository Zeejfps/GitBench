using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class DeleteRemoteBranchDialogViewModel : IDialogViewModel
{
    public AsyncCommand Delete { get; }
    public event Action? CloseRequested;

    public DeleteRemoteBranchDialogViewModel(
        DeleteRemoteBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Delete = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.DeleteRemoteBranch(request.Repo, request.RemoteName, request.BranchName);
                return outcome;
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
