using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

internal sealed class RemoveWorktreeDialogViewModel : IDialogViewModel
{
    private readonly IGitService _gitService;
    private readonly Repo _primary;
    private readonly string _worktreePath;
    private readonly Strings _strings;

    public State<bool> Force { get; } = new(false);

    public AsyncCommand Remove { get; }

    public event Action? CloseRequested;

    public RemoveWorktreeDialogViewModel(
        RemoveWorktreeRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc)
    {
        _gitService = gitService;
        _primary = request.Primary;
        _worktreePath = request.Worktree.Path;
        _strings = loc.Strings.Value;
        var primaryId = request.Primary.Id;

        Remove = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var force = Force.Value;
                var outcome = gitService.RemoveWorktree(_primary, _worktreePath, force);
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new WorktreesChangedMessage(primaryId));
                bus.Broadcast(new RefsChangedMessage(primaryId));
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
