using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Commits;

internal sealed class ResetCommitDialogViewModel : IDisposable
{
    public State<ResetMode> Mode { get; } = new(ResetMode.Mixed);

    public AsyncCommand Reset { get; }

    public event Action? CloseRequested;

    public ResetCommitDialogViewModel(
        ResetCommitRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        Reset = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.ResetCurrent(request.Repo, request.Sha, Mode.Value);
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

internal readonly record struct ResetCommitRequest(Repo Repo, string Sha);
