using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown when the user clicks Branch in the actions toolbar. Mirrors Fork's
/// "Create Branch" dialog: branch name + starting point (prefilled with the current HEAD's
/// branch name) + a "checkout after create" checkbox. Runs `git branch &lt;name&gt; &lt;start&gt;` or
/// `git checkout -b &lt;name&gt; &lt;start&gt;` depending on the checkbox.
/// </summary>
internal sealed class CreateBranchDialog : MultiChildView, IBind<CreateBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly TextInputView _nameInput;
    private readonly CheckoutDialogKbmController _nameController;
    private readonly TextInputView _startPointInput;
    private readonly CheckboxView _checkoutCheckbox;
    private readonly DialogButton _createButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public CreateBranchDialog(Repo repo, string suggestedStartPoint, Action onClose)
    {
        _onClose = onClose;

        var nameLabel = DialogFrame.Label("Branch name");

        _nameInput = DialogFrame.TextInput();
        var nameBox = DialogFrame.WrapInput(_nameInput);

        var startPointLabel = DialogFrame.Label("Starting point");

        _startPointInput = DialogFrame.TextInput();
        var startPointBox = DialogFrame.WrapInput(_startPointInput);

        var startPointHint = DialogFrame.Hint("Branch, tag, or commit SHA. Leave blank for HEAD.");

        _checkoutCheckbox = new CheckboxView("Check out after create")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _createButton = new DialogButton("Create") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Create branch", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                nameLabel,
                nameBox,
                startPointLabel,
                startPointBox,
                startPointHint,
                _checkoutCheckbox,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _createButton),
            },
        }));

        // Controllers go on the inputs (not the dialog) — see CheckoutBranchDialog for why:
        // BaseTextInputKbmController consumes left-press anywhere inside the view it's on,
        // so attaching to the outer dialog would swallow clicks meant for Cancel/Create.
        _nameController = new CheckoutDialogKbmController(_nameInput, _createButton.Command, onClose);
        _nameInput.UseController(_ => _nameController);
        _startPointInput.UseController(_ => new CheckoutDialogKbmController(_startPointInput, _createButton.Command, onClose));

        var request = new CreateBranchRequest(repo, suggestedStartPoint);
        this.UseViewModel(
            ctx => new CreateBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CreateBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _nameInput.BindTwoWay(vm.Name);
        _startPointInput.BindTwoWay(vm.StartPoint);
        _checkoutCheckbox.IsChecked.BindTwoWay(vm.Checkout);
        _createButton.BindBusyCommand(vm.Create);
        _cancelButton.DisableWhile(vm.Create.IsRunning);
        _errorView.BindText(vm.Create.Error, s => s ?? string.Empty);

        _nameController.BeginEditing();
    }
}
