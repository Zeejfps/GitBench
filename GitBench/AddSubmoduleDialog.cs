using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Modal shown from a primary RepoRow's "Add submodule…" menu. Collects the URL, path,
/// and optional tracked branch that `git submodule add` needs, plus a force toggle
/// for re-using a path that's been previously used.
/// </summary>
internal sealed class AddSubmoduleDialog : MultiChildView, IBind<AddSubmoduleDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _urlField;
    private readonly LabeledInputField _pathField;
    private readonly LabeledInputField _branchField;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogShell _shell;

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

        _shell = new DialogShell("Add submodule", onClose)
        {
            Action = ("Add", DialogButtonRole.Primary),
            Body = { _urlField, _pathField, _branchField, _forceCheckbox },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_urlField.Input, _pathField.Input, _branchField.Input);

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
        _shell.BindCommand(vm.Add);

        _shell.BeginEditing();
    }
}
