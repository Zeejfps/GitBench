using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
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

    protected override View CreateView(Context ctx)
    {
        var vm = new DeleteRemoteBranchDialogViewModel(
            new DeleteRemoteBranchRequest(Repo, RemoteName, BranchName),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var view = new Dialog
        {
            Title = "Delete remote branch",
            OnClose = OnClose,
            Action = ("Delete", DialogButtonRole.Destructive),
            Command = vm.Delete,
            ConfirmKeys = true,
            Body =
            [
                new ThemedText
                {
                    Value = $"Delete '{BranchName}' from remote '{RemoteName}'?",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.BodyText,
                },
                new ThemedText
                {
                    Value = "This is a network operation. Your local branches are not affected.",
                    Wrap = TextWrap.Wrap,
                    Color = s => s.DialogBody.RowTextMissing,
                },
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
    }
}
