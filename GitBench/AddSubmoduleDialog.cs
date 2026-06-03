using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown from a primary RepoRow's "Add submodule…" menu. Collects the URL, path,
/// and optional tracked branch that `git submodule add` needs, plus a force toggle
/// for re-using a path that's been previously used.
/// </summary>
internal sealed class AddSubmoduleDialog : MultiChildView, IBind<AddSubmoduleDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _urlField;
    private readonly CheckoutDialogKbmController _urlController;
    private readonly LabeledInputField _pathField;
    private readonly LabeledInputField _branchField;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogButton _addButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public AddSubmoduleDialog(Repo primary, Action onClose)
    {
        _onClose = onClose;

        _urlField = new LabeledInputField("Repository URL");

        _pathField = new LabeledInputField("Path inside parent")
        {
            Hint = "Where to clone the submodule, relative to the parent root.",
        };

        _branchField = new LabeledInputField("Track branch (optional)")
        {
            Hint = "Leave blank to pin to the upstream HEAD at clone time.",
        };

        _forceCheckbox = new CheckboxView("Force (allow paths previously used)")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _addButton = new DialogButton("Add") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Add submodule", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                _urlField,
                _pathField,
                _branchField,
                _forceCheckbox,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _addButton),
            },
        }));

        _urlController = new CheckoutDialogKbmController(_urlField.Input, _addButton.Command, onClose);
        _urlField.Input.UseController(_ => _urlController);
        _pathField.Input.UseController(_ => new CheckoutDialogKbmController(_pathField.Input, _addButton.Command, onClose));
        _branchField.Input.UseController(_ => new CheckoutDialogKbmController(_branchField.Input, _addButton.Command, onClose));

        var request = new AddSubmoduleViewRequest(primary);
        this.UseViewModel(
            ctx => new AddSubmoduleDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(AddSubmoduleDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _urlField.Input.BindTwoWay(vm.Url);
        _pathField.Input.BindTwoWay(vm.Path);
        _branchField.Input.BindTwoWay(vm.Branch);
        _branchField.BindStatus(vm.BranchStatus);
        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        _addButton.BindBusyCommand(vm.Add);
        _cancelButton.DisableWhile(vm.Add.IsRunning);
        _errorView.BindText(vm.Add.Error, s => s ?? string.Empty);

        _urlController.BeginEditing();
    }
}
