using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown from a primary RepoRow's "New worktree…" menu. Collects the three
/// fields `git worktree add` needs (path, start point, optional new branch name) plus
/// a force toggle for re-using an existing dirty path.
/// </summary>
internal sealed class CreateWorktreeDialog : MultiChildView, IBind<CreateWorktreeDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _pathField;
    private readonly CheckoutDialogKbmController _pathController;
    private readonly LabeledInputField _startPointField;
    private readonly LabeledInputField _branchField;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogButton _createButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private CreateWorktreeDialogViewModel? _vm;

    public CreateWorktreeDialog(Repo primary, Action onClose)
    {
        _onClose = onClose;

        var browseButton = new DialogButton("Browse…", PickPath)
        {
            Height = 28,
            Width = 80,
        };

        _pathField = new LabeledInputField("Worktree path")
        {
            Accessory = browseButton,
        };

        _startPointField = new LabeledInputField("Start point")
        {
            Hint = "Branch, tag, or commit SHA.",
        };

        _branchField = new LabeledInputField("New branch (optional)")
        {
            Hint = "Leave blank to check out the start point as-is.",
        };

        _forceCheckbox = new CheckboxView("Force (allow non-empty path or re-used branch)")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _createButton = new DialogButton("Create") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("New worktree", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                _pathField,
                _startPointField,
                _branchField,
                _forceCheckbox,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _createButton),
            },
        }));

        _pathController = new CheckoutDialogKbmController(_pathField.Input, _createButton.Command, onClose);
        _pathField.Input.UseController(_ => _pathController);
        _startPointField.Input.UseController(_ => new CheckoutDialogKbmController(_startPointField.Input, _createButton.Command, onClose));
        _branchField.Input.UseController(_ => new CheckoutDialogKbmController(_branchField.Input, _createButton.Command, onClose));

        var request = new CreateWorktreeRequest(primary);
        this.UseViewModel(
            ctx => new CreateWorktreeDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CreateWorktreeDialogViewModel vm)
    {
        _vm = vm;
        vm.CloseRequested += _onClose;

        _pathField.Input.BindTwoWay(vm.Path);
        _startPointField.Input.BindTwoWay(vm.StartPoint);
        _branchField.Input.BindTwoWay(vm.NewBranchName);
        _branchField.BindStatus(vm.NewBranchStatus);
        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        _createButton.BindBusyCommand(vm.Create);
        _cancelButton.DisableWhile(vm.Create.IsRunning);
        _errorView.BindText(vm.Create.Error, s => s ?? string.Empty);

        _pathController.BeginEditing();
    }

    private void PickPath()
    {
        var shell = Context?.Get<IPlatformShell>();
        var picked = shell?.PickFolder("Select worktree location");
        if (!string.IsNullOrEmpty(picked) && _vm != null)
        {
            _vm.Path.Value = picked;
        }
    }
}
