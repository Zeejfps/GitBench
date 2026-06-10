using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Stash;

internal sealed class DropStashViewModel : IDisposable
{
    public AsyncCommand Drop { get; }

    public event Action? CloseRequested;

    public DropStashViewModel(
        Repo repo,
        int index,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Drop = new AsyncCommand(
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
