using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Confirmation modal for deleting a branch from a remote. Calls
/// `git push &lt;remote&gt; --delete &lt;branch&gt;` — a network operation that doesn't touch
/// local branches. The server may refuse for protected refs; that error is surfaced.
/// </summary>
internal sealed class DeleteRemoteBranchDialog : MultiChildView, IBind<DeleteRemoteBranchDialogViewModel>
{
    private readonly DialogShell _shell;
    private readonly Action _onClose;

    public DeleteRemoteBranchDialog(Repo repo, string remoteName, string branchName, Action onClose)
    {
        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Delete '{branchName}' from remote '{remoteName}'?",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        var hint = DialogFrame.Hint(
            "This is a network operation. Your local branches are not affected.",
            TextWrap.Wrap);

        _shell = new DialogShell("Delete remote branch", onClose)
        {
            Action = ("Delete", DialogButtonRole.Destructive),
            Body = { prompt, hint },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        var request = new DeleteRemoteBranchRequest(repo, remoteName, branchName);
        this.UseViewModel(
            ctx => new DeleteRemoteBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeleteRemoteBranchDialogViewModel vm)
    {
        _shell.BindCommand(vm.Delete);
        vm.CloseRequested += _onClose;
    }
}
