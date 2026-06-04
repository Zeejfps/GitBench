using ZGF.Observable;

namespace GitGui;

internal sealed class DeleteStashViewModel : IDisposable
{
    public AsyncCommand Delete { get; }

    public event Action? CloseRequested;

    public DeleteStashViewModel(
        Repo repo,
        int index,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Delete = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.DropStash(repo, index);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Stash drop failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}
