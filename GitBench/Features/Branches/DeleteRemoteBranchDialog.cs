using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Confirmation modal for deleting a branch from a remote. Calls
/// `git push &lt;remote&gt; --delete &lt;branch&gt;` — a network operation that doesn't touch
/// local branches. The server may refuse for protected refs; that error is surfaced.
/// </summary>
internal sealed record DeleteRemoteBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string RemoteName { get; init; }
    public required string BranchName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new DeleteRemoteBranchDialogViewModel(
            new DeleteRemoteBranchRequest(Repo, RemoteName, BranchName),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = "Delete remote branch",
            OnClose = OnClose,
            ViewModel = vm,
            Action = ("Delete", DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = $"Delete '{BranchName}' from remote '{RemoteName}'?",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Text
                {
                    Value = "This is a network operation. Your local branches are not affected.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
            ],
        };
    }
}
