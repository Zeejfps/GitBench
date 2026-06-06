using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Confirmation modal for aborting an in-progress op (merge / rebase / cherry-pick /
/// revert / am / bisect) or recovering from a stash-apply conflict via `git reset --merge`.
/// All variants are destructive — any in-progress conflict resolutions and (for
/// reset --merge) conflicting worktree edits are thrown away — so the user confirms first.
/// </summary>
internal sealed class AbortOperationDialog : MultiChildView, IBind<AbortOperationDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    public AbortOperationDialog(Repo repo, RepoOperationState state, Action onClose)
    {
        _onClose = onClose;

        var (titleText, bodyText) = CopyFor(state);
        var confirmLabel = AbortOperationDialogViewModel.DefaultConfirmLabel(state);

        var prompt = new TextView
        {
            Text = bodyText,
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell(titleText, onClose)
        {
            Action = (confirmLabel, DialogButtonRole.Destructive),
            Body = { new FlexItem { Grow = 1, Child = prompt } },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        var request = new AbortOperationRequest(repo, state);
        this.UseViewModel(
            ctx => new AbortOperationDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(AbortOperationDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _shell.ActionButton.BindBusyCommand(vm.Abort);
        _shell.CancelButton.DisableWhile(vm.Abort.IsRunning);
        _shell.Error.BindText(vm.Error, s => s ?? string.Empty);

        vm.ConfirmButtonLabel.Subscribe(label => _shell.ActionButton.Label = label);
    }

    private static (string Title, string Body) CopyFor(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => (
            "Abort merge?",
            "Aborts the in-progress merge and restores the working tree to the pre-merge state. Any conflict resolutions you've made will be lost."),
        RepoOperationState.Rebase => (
            "Abort rebase?",
            "Aborts the in-progress rebase and returns HEAD to the branch's original tip. Any conflict resolutions you've made will be lost."),
        RepoOperationState.CherryPick => (
            "Abort cherry-pick?",
            "Aborts the in-progress cherry-pick and restores the working tree to the pre-cherry-pick state."),
        RepoOperationState.Revert => (
            "Abort revert?",
            "Aborts the in-progress revert and restores the working tree to the pre-revert state."),
        RepoOperationState.ApplyMailbox => (
            "Abort patch apply?",
            "Aborts the in-progress `git am` and restores the working tree to the pre-apply state. The mailbox queue is discarded."),
        RepoOperationState.Bisect => (
            "Reset bisect?",
            "Ends the bisect session and returns HEAD to where it was when bisect started."),
        RepoOperationState.UnmergedPaths => (
            "Reset unmerged paths?",
            "Discards conflicting worktree changes and clears the unmerged index entries, returning the conflicted files to HEAD. Clean local changes are kept; in-progress conflict resolutions are lost."),
        _ => ("Abort?", "Cancel the in-progress operation."),
    };
}
