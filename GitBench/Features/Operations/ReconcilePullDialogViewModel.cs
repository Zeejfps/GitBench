using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Operations;

internal sealed class ReconcilePullDialogViewModel : IDisposable
{
    public State<PullStrategy> Strategy { get; } = new(PullStrategy.Merge);

    public AsyncCommand Pull { get; }
    public IReadable<string?> Error => Pull.Error;

    public event Action? CloseRequested;

    public ReconcilePullDialogViewModel(
        Repo repo,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Pull = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.Pull(repo, Strategy.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Pull failed.");
            },
            onSuccess: () =>
            {
                CloseRequested?.Invoke();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
            });
    }

    public void Dispose() { }
}
