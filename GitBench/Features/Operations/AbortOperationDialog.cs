using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
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

    protected override IWidget Build(Context ctx)
    {
        var vm = new AbortOperationDialogViewModel(
            new AbortOperationRequest(Repo, State),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>(),
            ctx.Localization());

        var s = ctx.Localization().Strings.Value;
        var (titleText, bodyText) = CopyFor(s, State);

        return new Dialog
        {
            Title = titleText,
            OnClose = OnClose,
            Action = (AbortOperationDialogViewModel.DefaultConfirmLabel(s, State), DialogButtonRole.Destructive),
            Command = vm.Abort,
            Error = vm.Error,
            BindActionLabel = vm.ConfirmButtonLabel,
            ConfirmKeys = true,
            ViewModel = vm,
            Body =
            [
                new Text
                {
                    Value = bodyText,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
            ],
        };
    }

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
