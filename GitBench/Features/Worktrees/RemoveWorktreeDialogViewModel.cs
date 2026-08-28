using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

internal sealed class RemoveWorktreeDialogViewModel : IDialogViewModel
{
    private readonly IGitWorktreeOperations _gitService;
    private readonly Repo _primary;
    private readonly string _worktreePath;
    private readonly Strings _strings;

    // Written by the command's background work, read by its completion on the UI thread.
    private WorktreeRemoveOutcome? _outcome;

    public State<bool> Force { get; } = new(false);

    public AsyncCommand Remove { get; }

    public event Action? CloseRequested;

    public RemoveWorktreeDialogViewModel(
        RemoveWorktreeRequest request,
        IGitWorktreeOperations gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IPlatformShell shell,
        ILocalizationService loc)
    {
        _gitService = gitService;
        _primary = request.Primary;
        _worktreePath = request.Worktree.Path;
        _strings = loc.Strings.Value;
        var primaryId = request.Primary.Id;

        Remove = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => _outcome = gitService.RemoveWorktree(_primary, _worktreePath, Force.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new WorktreesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));

                // The worktree is gone either way — only its directory survived — so this closes
                // like a success and reports the leftovers as something to clean up, not as a
                // failed removal.
                if (_outcome is WorktreeRemoveOutcome.RemovedWithLeftovers left)
                    bus.Broadcast(new ShowToastMessage(ToastIntent.Warning(
                        _strings.WorktreesRemovedWithLeftovers(left.Path, left.Reason),
                        new ToastAction(_strings.WorktreesLeftoversOpenAction, () => shell.OpenFolder(left.Path)))));

                CloseRequested?.Invoke();
            });
    }

    /// <summary>
    /// A worktree that was `git worktree lock`ed can't be removed until it's unlocked. Git names it
    /// "locked working tree" in the failure (but not the path — hence a VM-supplied recovery, since we
    /// hold the path here). Offers a one-click `git worktree unlock`; the user then retries the remove.
    /// </summary>
    public OperationErrorRecovery? UnlockRecoveryFor(string error)
    {
        if (!error.Contains("locked working tree", StringComparison.OrdinalIgnoreCase))
            return null;

        return new OperationErrorRecovery(
            _strings.WorktreesUnlockAction,
            _strings.WorktreesUnlockedStatus,
            () => _gitService.UnlockWorktree(_primary, _worktreePath).FailureMessage);
    }

    public void Dispose() { }
}

internal readonly record struct RemoveWorktreeRequest(Repo Primary, Repo Worktree);
