using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

internal sealed class DeleteTagDialogViewModel : IDialogViewModel
{
    public State<bool> DeleteFromRemotes { get; } = new(false);

    public AsyncCommand Delete { get; }

    public event Action? CloseRequested;

    public DeleteTagDialogViewModel(
        DeleteTagRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var repoId = request.Repo.Id;

        Delete = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.DeleteTag(request.Repo, request.TagName, DeleteFromRemotes.Value);
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repoId));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}

internal readonly record struct DeleteTagRequest(Repo Repo, string TagName);
