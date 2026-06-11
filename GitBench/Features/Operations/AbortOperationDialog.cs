using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
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

    protected override View CreateView(Context ctx)
    {
        var vm = new AbortOperationDialogViewModel(
            new AbortOperationRequest(Repo, State),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var (titleText, bodyText) = CopyFor(State);

        var prompt = new ThemedText
        {
            Value = bodyText,
            Wrap = TextWrap.Wrap,
            Color = s => s.DialogBody.BodyText,
        }.BuildView(ctx);

        var shell = new DialogShell(titleText, OnClose)
        {
            Action = (AbortOperationDialogViewModel.DefaultConfirmLabel(State), DialogButtonRole.Destructive),
            Body = { new FlexItem { Grow = 1, Child = prompt } },
        };

        var root = new ContainerView();
        root.Children.Add(shell.View);

        shell.BindCommand(vm.Abort, vm.Error);
        root.Bind(vm.ConfirmButtonLabel, label => shell.ActionButton.Label = label);
        root.UseController(ctx.Require<InputSystem>(),
            () => new DialogKbmController(() => shell.ActionButton.PerformClick(), OnClose));

        root.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return root;
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
