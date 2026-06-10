using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Branches;

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
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogShell _shell;

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

        _shell = new DialogShell("Rename branch", onClose)
        {
            Action = ("Rename", DialogButtonRole.Primary),
            Body = { subtitle, _nameField, _forceCheckbox },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_nameField.Input);

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
        _shell.BindCommand(vm.Rename);

        _nameField.Input.SelectAll();
        _shell.BeginEditing();
    }
}
