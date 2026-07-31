using GitBench.Features.Repos;
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
        IMessageBus bus,
        IRepoHeadStore head)
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
            },
            // `checkout -B` lands HEAD on the branch it just moved — a detached HEAD becomes an
            // attached one, which every reader of "current branch" needs to know is coming.
            onStart: () => head.BeginMove(request.Repo, request.BranchName));
    }

    public void Dispose() { }
}

internal readonly record struct MoveBranchRequest(Repo Repo, string BranchName, string Sha);
