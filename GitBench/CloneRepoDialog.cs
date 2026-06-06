using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Modal shown from the "Add Repository" menu's Clone entry. Collects a remote URL, a parent
/// directory (with a Browse button), and the subfolder name, then runs <c>git clone</c> and
/// opens the result. See <see cref="CloneRepoDialogViewModel"/>.
/// </summary>
internal sealed class CloneRepoDialog : MultiChildView, IBind<CloneRepoDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _urlField;
    private readonly LabeledInputField _locationField;
    private readonly LabeledInputField _nameField;
    private readonly DialogShell _shell;
    private CloneRepoDialogViewModel? _vm;

    public CloneRepoDialog(Action onClose)
    {
        _onClose = onClose;

        _urlField = new LabeledInputField("Repository URL")
        {
            Placeholder = "https://github.com/user/repo.git",
        };

        // No fixed Width — DialogButton sizes to its label (it carries its own 16px horizontal
        // padding), so pinning a width clips "Browse…".
        var browseButton = new DialogButton("Browse…", PickLocation)
        {
            Height = 28,
        };
        _locationField = new LabeledInputField("Clone into")
        {
            Accessory = browseButton,
            Hint = "Parent folder. The repository is cloned into a new subfolder here.",
        };

        _nameField = new LabeledInputField("Folder name");

        _shell = new DialogShell("Clone repository", onClose)
        {
            Action = ("Clone", DialogButtonRole.Primary),
            Body = { _urlField, _locationField, _nameField },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_urlField.Input, _locationField.Input, _nameField.Input);

        this.UseViewModel(
            ctx => new CloneRepoDialogViewModel(
                ctx.Require<IGitService>(),
                ctx.Require<IRepoRegistry>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CloneRepoDialogViewModel vm)
    {
        _vm = vm;
        vm.CloseRequested += _onClose;

        _urlField.Input.BindTwoWay(vm.Url);
        _locationField.Input.BindTwoWay(vm.ParentDir);
        _nameField.Input.BindTwoWay(vm.FolderName);
        _shell.BindCommand(vm.Clone);

        _shell.BeginEditing();
    }

    private void PickLocation()
    {
        var shell = Context?.Get<IPlatformShell>();
        var picked = shell?.PickFolder("Choose where to clone");
        if (!string.IsNullOrEmpty(picked) && _vm != null)
            _vm.ParentDir.Value = picked;
    }
}
