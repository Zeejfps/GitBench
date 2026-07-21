using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Operations;

internal sealed class ReconcilePullDialogViewModel : IDialogViewModel
{
    public State<PullStrategy> Strategy { get; } = new(PullStrategy.Merge);

    public AsyncCommand Pull { get; }

    public event Action? CloseRequested;

    public ReconcilePullDialogViewModel(
        Repo repo,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Pull = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.Pull(repo, Strategy.Value);
                return outcome;
            },
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
            });
    }

    public void Dispose() { }
}
