using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Branches;

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
                return outcome is GitOutcome.Failed failed ? failed.Message : null;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}
