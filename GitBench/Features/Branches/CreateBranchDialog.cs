using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Observable;

namespace GitBench.Features.Branches;

/// <summary>
/// Modal shown when the user clicks Branch in the actions toolbar. Mirrors Fork's
/// "Create Branch" dialog: branch name + starting point (prefilled with the current HEAD's
/// branch name) + a "checkout after create" checkbox. Runs `git branch &lt;name&gt; &lt;start&gt;` or
/// `git checkout -b &lt;name&gt; &lt;start&gt;` depending on the checkbox.
/// </summary>
internal sealed class CreateBranchDialog : MultiChildView, IBind<CreateBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _nameField;
    private readonly LabeledInputField _startPointField;
    private readonly CheckboxView _checkoutCheckbox;
    private readonly DialogShell _shell;

    public CreateBranchDialog(Repo repo, string suggestedStartPoint, Action onClose)
    {
        _onClose = onClose;

        _nameField = new LabeledInputField("Branch name");
        _startPointField = new LabeledInputField("Starting point")
        {
            Hint = "Branch, tag, or commit SHA. Leave blank for HEAD.",
        };
        _checkoutCheckbox = new CheckboxView("Check out after create") { Height = 22 };

        _shell = new DialogShell("Create branch", onClose)
        {
            Action = ("Create", DialogButtonRole.Primary),
            Body = { _nameField, _startPointField, _checkoutCheckbox },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_nameField.Input, _startPointField.Input);

        this.UseViewModel(
            ctx => new CreateBranchDialogViewModel(
                repo,
                suggestedStartPoint,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CreateBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _shell.BindCommand(vm.Create);
        _nameField.Input.BindTwoWay(vm.Name);
        _nameField.BindStatus(vm.NameStatus);
        _startPointField.Input.BindTwoWay(vm.StartPoint);
        _checkoutCheckbox.IsChecked.BindTwoWay(vm.Checkout);

        _shell.BeginEditing();
    }
}
