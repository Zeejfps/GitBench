using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Confirmation modal for "Reset &lt;branch&gt; to here" when the move is NOT a fast-forward —
/// the branch has commits that aren't in the selected revision, so force-moving it would
/// leave those commits unreachable. The fast-forward case skips this dialog (applied directly
/// by <see cref="CommitsViewModel"/>). Runs `git checkout -B &lt;branch&gt; &lt;sha&gt;` on confirm.
/// </summary>
internal sealed class MoveBranchDialog : MultiChildView, IBind<MoveBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    public MoveBranchDialog(Repo repo, string branchName, string sha, string shortSha, string summary, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Reset '{branchName}' to the selected revision and check it out",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var commitLine = new TextView
        {
            Text = string.IsNullOrEmpty(summary) ? shortSha : $"{shortSha}  {summary}",
            TextWrap = TextWrap.NoWrap,
        };
        commitLine.BindThemedTextColor(s => s.DialogBody.BodyText);
        var commitClip = new ClippingView { Children = { commitLine } };

        var warning = DialogFrame.Hint(
            $"'{branchName}' has commits that aren't in {shortSha}. Resetting it here will leave those commits unreachable from any branch.",
            TextWrap.Wrap);

        _shell = new DialogShell("Reset branch to revision", onClose)
        {
            Width = DialogFrame.WidthWide,
            Action = ("Reset branch", DialogButtonRole.Destructive),
            Body = { subtitle, commitClip, warning },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        var request = new MoveBranchRequest(repo, branchName, sha);
        this.UseViewModel(
            ctx => new MoveBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(MoveBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _shell.BindCommand(vm.Move);
    }
}
