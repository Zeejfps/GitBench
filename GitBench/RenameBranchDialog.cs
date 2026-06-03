using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown when the user picks "Rename…" on a local branch row. Full branch path is
/// editable (slashes allowed) so cross-folder moves like feature/login → bugs/login work
/// the same as in `git branch -m`. The force checkbox switches the underlying call to -M,
/// allowing the rename to overwrite an existing branch of the new name.
/// </summary>
internal sealed class RenameBranchDialog : MultiChildView, IBind<RenameBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _nameField;
    private readonly CheckoutDialogKbmController _nameController;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogButton _renameButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public RenameBranchDialog(Repo repo, string currentName, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Renaming '{currentName}'",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        _nameField = new LabeledInputField("New name");

        _forceCheckbox = new CheckboxView("Force rename even if target exists")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _renameButton = new DialogButton("Rename") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Rename branch", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                _nameField,
                _forceCheckbox,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _renameButton),
            },
        }));

        _nameController = new CheckoutDialogKbmController(_nameField.Input, _renameButton.Command, onClose);
        _nameField.Input.UseController(_ => _nameController);

        var request = new RenameBranchRequest(repo, currentName);
        this.UseViewModel(
            ctx => new RenameBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(RenameBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _nameField.Input.BindTwoWay(vm.Name);
        _nameField.BindStatus(vm.NameStatus);
        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        _renameButton.BindBusyCommand(vm.Rename);
        _cancelButton.DisableWhile(vm.Rename.IsRunning);
        _errorView.BindText(vm.Rename.Error, s => s ?? string.Empty);

        _nameField.Input.SelectAll();
        _nameController.BeginEditing();
    }
}
