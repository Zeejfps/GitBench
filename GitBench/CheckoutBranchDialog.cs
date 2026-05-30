using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown when the user double-clicks a remote branch that has no matching local
/// branch. Lets them pick the local branch name and whether to set up tracking, then
/// runs `git checkout -b &lt;local&gt; [--track|--no-track] &lt;remote&gt;/&lt;branch&gt;`.
/// </summary>
internal sealed class CheckoutBranchDialog : MultiChildView, IBind<CheckoutBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly TextInputView _nameInput;
    private readonly CheckoutDialogKbmController _nameController;
    private readonly CheckboxView _trackCheckbox;
    private readonly DialogButton _cancelButton;
    private readonly DialogButton _checkoutButton;

    public CheckoutBranchDialog(
        Repo repo,
        string remoteName,
        string remoteBranchName,
        string suggestedLocalName,
        Action onClose)
    {
        Width = 420f;

        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Create a local branch from {remoteName}/{remoteBranchName}",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var nameLabel = DialogFrame.Label("Local branch name");

        _nameInput = DialogFrame.TextInput();
        var nameBox = DialogFrame.WrapInput(_nameInput);

        _trackCheckbox = new CheckboxView("Track this remote branch")
        {
            Height = 22,
        };

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _checkoutButton = new DialogButton("Checkout") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Checkout branch", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                nameLabel,
                nameBox,
                _trackCheckbox,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _checkoutButton),
            },
        }));

        // Controller goes on the INPUT's Behaviors, not the outer dialog's:
        // BaseTextInputKbmController.OnMouseButtonStateChanged consumes left-press events
        // anywhere inside the view it's attached to, so putting it on the dialog would
        // swallow clicks meant for the Cancel/Checkout buttons.
        _nameController = new CheckoutDialogKbmController(_nameInput, _checkoutButton.Command, onClose);
        _nameInput.UseController(_ => _nameController);

        var request = new CheckoutRequest(repo, remoteName, remoteBranchName, suggestedLocalName);
        this.UseViewModel(
            ctx => new CheckoutBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CheckoutBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _nameInput.BindTwoWay(vm.Name);
        _trackCheckbox.IsChecked.BindTwoWay(vm.Track);
        _checkoutButton.BindBusyCommand(vm.Checkout);
        _cancelButton.DisableWhile(vm.Checkout.IsRunning);

        // Bind runs after the input is attached to a context — doing focus earlier (e.g.
        // in the dialog ctor) produced an empty-looking field, since StartEditing/StealFocus
        // hadn't run yet and caret/selection visuals were stale.
        _nameInput.SelectAll();
        _nameController.BeginEditing();
    }
}
