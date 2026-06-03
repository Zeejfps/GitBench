using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class ForcePushDialog : MultiChildView, IBind<ForcePushDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    public ForcePushDialog(Repo repo, string branchName, int ahead, int behind, Action onClose)
    {
        _onClose = onClose;

        var displayBranch = string.IsNullOrEmpty(branchName) ? "this branch" : $"'{branchName}'";
        var prompt = new TextView
        {
            Text = $"{displayBranch} has diverged from its upstream — {ahead} ahead, {behind} behind. "
                 + "A regular push will be rejected. Force-push (with lease) will overwrite the remote "
                 + "branch with your local history; any commits on the remote that you haven't fetched "
                 + "will be lost. The lease refuses the push if the remote moved since your last fetch.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell("Force push?", onClose)
        {
            Action = ("Force push", DialogButtonRole.Destructive),
            Body = { new FlexItem { Grow = 1, Child = prompt } },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        this.UseViewModel(
            ctx => new ForcePushDialogViewModel(
                repo,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(ForcePushDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _shell.BindCommand(vm.ForcePush);
    }
}
