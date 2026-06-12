using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class MoveBranchDialogViewModel : IDialogViewModel
{
    public AsyncCommand Move { get; }

    public event Action? CloseRequested;

    public MoveBranchDialogViewModel(
        MoveBranchRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Move = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.MoveBranch(request.Repo, request.BranchName, request.Sha, checkout: true);
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}

internal readonly record struct MoveBranchRequest(Repo Repo, string BranchName, string Sha);
