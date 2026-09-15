using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Confirmation modal for aborting an in-progress op (merge / rebase / cherry-pick /
/// revert / am / bisect) or recovering from a stash-apply conflict via `git reset --merge`.
/// All variants are destructive — any in-progress conflict resolutions and (for
/// reset --merge) conflicting worktree edits are thrown away — so the user confirms first.
/// </summary>
internal sealed record AbortOperationDialog : Widget
{
    public required Repo Repo { get; init; }
    public required RepoOperationState State { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var state = State;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitIntegrationOperations>();
        var bus = ctx.Require<IMessageBus>();
        var s = ctx.Localization().Strings.Value;

        var forceQuitMode = new State<bool>(false);
        // Plain local, not a State<T>: written in the background work lambda and read back in the
        // UI-thread onError callback (which runs after work completes). It drives no binding — only
        // forceQuitMode does — so it needs no notifications, and a plain assignment avoids firing
        // them off the worker thread.
        var forceQuitAvailable = false;

        var defaultLabel = DefaultConfirmLabel(s, state);
        var confirmButtonLabel = new Derived<string>(() => forceQuitMode.Value ? s.OperationsAbortForceClear : defaultLabel);
        var gate = new Derived<bool>(() => state != RepoOperationState.None);

        var abort = new AsyncCommand(
            ctx.Require<IUiDispatcher>(),
            work: () =>
            {
                var outcome = gitService.AbortOperation(repo, state, forceQuitMode.Value);
                if (outcome is AbortOutcome.Failed failed)
                {
                    forceQuitAvailable = failed.ForceQuitAvailable;
                    return failed.Message;
                }
                forceQuitAvailable = false;
                return null;
            },
            onSuccess: () =>
            {
                onClose();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
            },
            gate: gate,
            onError: _ =>
            {
                // A first failure that reports force-quit availability flips the button into
                // "Force clear" mode so a second press can hard-clear the operation state.
                if (forceQuitAvailable && !forceQuitMode.Value)
                    forceQuitMode.Value = true;
            });

        var (titleText, bodyText) = CopyFor(s, state);

        return new Dialog
        {
            Title = titleText,
            OnClose = onClose,
            Action = (defaultLabel, DialogButtonRole.Destructive),
            Command = abort,
            BindActionLabel = confirmButtonLabel,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = bodyText },
            ],
        };
    }

    private static string DefaultConfirmLabel(Strings s, RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => s.OperationsAbortMerge,
        RepoOperationState.Rebase => s.OperationsAbortRebase,
        RepoOperationState.CherryPick => s.OperationsAbortCherryPick,
        RepoOperationState.Revert => s.OperationsAbortRevert,
        RepoOperationState.ApplyMailbox => s.OperationsAbortApply,
        RepoOperationState.Bisect => s.OperationsAbortBisect,
        RepoOperationState.UnmergedPaths => s.OperationsAbortUnmerged,
        _ => s.CommonAbort,
    };

    private static (string Title, string Body) CopyFor(Strings s, RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => (
            s.OperationsAbortDialogTitleMerge,
            s.OperationsAbortDialogBodyMerge),
        RepoOperationState.Rebase => (
            s.OperationsAbortDialogTitleRebase,
            s.OperationsAbortDialogBodyRebase),
        RepoOperationState.CherryPick => (
            s.OperationsAbortDialogTitleCherryPick,
            s.OperationsAbortDialogBodyCherryPick),
        RepoOperationState.Revert => (
            s.OperationsAbortDialogTitleRevert,
            s.OperationsAbortDialogBodyRevert),
        RepoOperationState.ApplyMailbox => (
            s.OperationsAbortDialogTitleApply,
            s.OperationsAbortDialogBodyApply),
        RepoOperationState.Bisect => (
            s.OperationsAbortDialogTitleBisect,
            s.OperationsAbortDialogBodyBisect),
        RepoOperationState.UnmergedPaths => (
            s.OperationsAbortDialogTitleUnmerged,
            s.OperationsAbortDialogBodyUnmerged),
        _ => (s.OperationsAbortDialogTitleDefault, s.OperationsAbortDialogBodyDefault),
    };
}
