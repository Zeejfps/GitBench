using ZGF.Observable;

namespace GitBench;

internal sealed class DeleteTagDialogViewModel : IDisposable
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

        Delete = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.DeleteTag(request.Repo, request.TagName, DeleteFromRemotes.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Delete tag failed.");
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
