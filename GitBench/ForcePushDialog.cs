using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class ForcePushDialog : MultiChildView, IBind<ForcePushDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _forcePushButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public ForcePushDialog(Repo repo, string branchName, int ahead, int behind, Action onClose)
    {
        Width = 520f;

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

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _forcePushButton = new DialogButton("Force push") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Force push?", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = prompt },
                _errorView,
                DialogFrame.ButtonsRow(_cancelButton, _forcePushButton),
            },
        }));

        this.UseController(_ => new DialogKbmController(_forcePushButton.Command, onClose));

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
        _forcePushButton.BindBusyCommand(vm.ForcePush);
        _cancelButton.DisableWhile(vm.ForcePush.IsRunning);
        _errorView.BindText(vm.ForcePush.Error, s => s ?? string.Empty);
    }
}
