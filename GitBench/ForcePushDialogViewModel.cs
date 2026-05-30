using ZGF.Observable;

namespace GitGui;

internal sealed class ForcePushDialogViewModel : IDisposable
{
    public AsyncCommand ForcePush { get; }

    public event Action? CloseRequested;

    public ForcePushDialogViewModel(
        Repo repo,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        ForcePush = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.Push(repo, force: true);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Force push failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}
