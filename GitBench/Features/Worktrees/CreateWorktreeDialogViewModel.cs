using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

internal sealed class CreateWorktreeDialogViewModel : IDisposable
{
    public State<string> Path { get; } = new(string.Empty);
    public State<string> StartPoint { get; } = new("HEAD");
    public State<string> NewBranchName { get; } = new(string.Empty);
    public State<bool> Force { get; } = new(false);

    /// <summary>Live refname validation for the optional new-branch field. Blank stays neutral
    /// (the field is optional); a typed-but-invalid name reports an error. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> NewBranchStatus { get; }

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    public CreateWorktreeDialogViewModel(
        CreateWorktreeRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;

        // New branch is optional, so blank is valid (RefNameRules treats empty as neutral);
        // a non-blank name must still be a legal refname before Create enables.
        NewBranchStatus = new Derived<FieldStatus?>(() => RefNameRules.Validate(NewBranchName.Value.Trim(), "Branch"));
        var gate = new Derived<bool>(() =>
            Path.Value.Trim().Length > 0 && StartPoint.Value.Trim().Length > 0
            && RefNameRules.Validate(NewBranchName.Value.Trim(), "Branch") is null);

        Create = AsyncCommand.ForOutcome(
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
                return outcome;
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
