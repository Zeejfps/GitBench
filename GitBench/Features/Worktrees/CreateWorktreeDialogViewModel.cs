using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

internal sealed class CreateWorktreeDialogViewModel : IDialogViewModel
{
    public State<string> Path { get; } = new(string.Empty);
    public State<string> StartPoint { get; } = new("HEAD");
    public State<string> NewBranchName { get; } = new(string.Empty);
    public State<bool> Force { get; } = new(false);
    public State<bool> InitSubmodules { get; } = new(true);
    public State<bool> RecurseSubmodules { get; } = new(true);

    /// <summary>Live refname validation for the optional new-branch field. Blank stays neutral
    /// (the field is optional); a typed-but-invalid name reports an error. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> NewBranchStatus { get; }

    public AsyncCommand Create { get; }

    public event Action? CloseRequested;

    // The path we last derived ourselves, so a manual edit sticks: the branch name only rewrites
    // the field while it still holds exactly what we put there. Mirrors CloneRepoDialogViewModel.
    private string _lastAutoPath = string.Empty;
    private string? _warning;

    public CreateWorktreeDialogViewModel(
        CreateWorktreeRequest request,
        IGitWorktreeOperations gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        Func<string, bool>? directoryExists = null)
    {
        var primaryId = request.Primary.Id;
        var exists = directoryExists ?? Directory.Exists;

        NewBranchName.Subscribe(branch =>
        {
            if (Path.Value != _lastAutoPath) return; // user took over the field
            var derived = WorktreePathDefaults.For(request.Primary.Path, branch, exists);
            Path.Value = derived;
            _lastAutoPath = derived;
        });

        // New branch is optional, so blank is valid (RefNameRules treats empty as neutral);
        // a non-blank name must still be a legal refname before Create enables.
        NewBranchStatus = new Derived<FieldStatus?>(() =>
        {
            var s = loc.Strings.Value;
            return RefNameRules.Validate(NewBranchName.Value.Trim(), s, s.RefnameNounBranch);
        });
        var gate = new Derived<bool>(() =>
            Path.Value.Trim().Length > 0 && StartPoint.Value.Trim().Length > 0
            && RefNameRules.IsValid(NewBranchName.Value.Trim()));

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
                    Force: force,
                    InitSubmodules: InitSubmodules.Value,
                    RecurseSubmodules: RecurseSubmodules.Value);
                var outcome = gitService.AddWorktree(request.Primary, req);
                _warning = (outcome as WorktreeAddOutcome.Added)?.Warning;
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new WorktreesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));
                CloseRequested?.Invoke();
                // The worktree exists either way — a submodule step that failed is reported after
                // the close, so it reads as "here is your worktree, and git said something about
                // its submodules" rather than as the create having failed.
                if (_warning is { Length: > 0 } warning)
                    bus.Broadcast(new ShowOperationErrorMessage(loc.Strings.Value.WorktreesCreateWarningTitle, warning));
            },
            gate: gate);
    }

    public void Dispose() { }
}

internal readonly record struct CreateWorktreeRequest(Repo Primary);
