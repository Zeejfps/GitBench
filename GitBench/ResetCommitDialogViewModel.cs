using ZGF.Observable;

namespace GitGui;

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
        Reset = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.ResetCurrent(request.Repo, request.Sha, Mode.Value);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Reset failed.");
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
