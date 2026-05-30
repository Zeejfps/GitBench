using ZGF.Observable;

namespace GitGui;

internal sealed class RemoveWorktreeDialogViewModel : IDisposable
{
    public State<bool> Force { get; } = new(false);

    public AsyncCommand Remove { get; }

    public event Action? CloseRequested;

    public RemoveWorktreeDialogViewModel(
        RemoveWorktreeRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var worktreePath = request.Worktree.Path;
        var primaryId = request.Primary.Id;

        Remove = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var force = Force.Value;
                var outcome = gitService.RemoveWorktree(request.Primary, worktreePath, force);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Remove worktree failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new WorktreesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));
                CloseRequested?.Invoke();
            });
    }

    public void Dispose() { }
}

internal readonly record struct RemoveWorktreeRequest(Repo Primary, Repo Worktree);
