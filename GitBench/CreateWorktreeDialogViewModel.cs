using ZGF.Observable;

namespace GitGui;

internal sealed class CreateWorktreeDialogViewModel : IDisposable
{
    public State<string> Path { get; } = new(string.Empty);
    public State<string> StartPoint { get; } = new("HEAD");
    public State<string> NewBranchName { get; } = new(string.Empty);
    public State<bool> Force { get; } = new(false);

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    public CreateWorktreeDialogViewModel(
        CreateWorktreeRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;

        var gate = new Derived<bool>(() =>
            Path.Value.Trim().Length > 0 && StartPoint.Value.Trim().Length > 0);

        Create = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var path = Path.Value.Trim();
                var startPoint = StartPoint.Value.Trim();
                var newBranch = NewBranchName.Value.Trim();
                var force = Force.Value;
                var req = new WorktreeAddRequest(
                    Path: path,
                    StartPoint: startPoint,
                    NewBranchName: newBranch.Length > 0 ? newBranch : null,
                    Force: force);
                var outcome = gitService.AddWorktree(request.Primary, req);
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Create worktree failed.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new WorktreesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void Dispose() { }
}

internal readonly record struct CreateWorktreeRequest(Repo Primary);
