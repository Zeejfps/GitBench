using ZGF.Gui.Views;
using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Observable;

namespace GitBench.Features.Worktrees;

/// <summary>
/// Modal shown from a primary RepoRow's "New worktree…" menu. Collects the three
/// fields `git worktree add` needs (path, start point, optional new branch name) plus
/// a force toggle for re-using an existing dirty path.
/// </summary>
internal sealed class CreateWorktreeDialog : ContainerView, IBind<CreateWorktreeDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _pathField;
    private readonly LabeledInputField _startPointField;
    private readonly LabeledInputField _branchField;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogShell _shell;
    private CreateWorktreeDialogViewModel? _vm;

    public CreateWorktreeDialog(Repo primary, Action onClose)
    {
        _onClose = onClose;

        var browseButton = new DialogButton("Browse…", PickPath)
        {
            Height = 28,
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

        _shell = new DialogShell("New worktree", onClose)
        {
            Action = ("Create", DialogButtonRole.Primary),
            Body = { _pathField, _startPointField, _branchField, _forceCheckbox },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_pathField.Input, _startPointField.Input, _branchField.Input);

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
        _shell.BindCommand(vm.Create);

        _shell.BeginEditing();
    }

    private void PickPath()
    {
        var shell = this.Context?.Get<IPlatformShell>();
        var picked = shell?.PickFolder("Select worktree location");
        if (!string.IsNullOrEmpty(picked) && _vm != null)
        {
            _vm.Path.Value = picked;
        }
    }
}
