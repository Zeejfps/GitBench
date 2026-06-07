using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

// Add/edit form for a single identity profile: a display name, the author name/email injected
// per commit, an optional SSH key, and a single host/owner match rule. Power users can add
// multiple match rules by editing identity-profiles.json directly; the form keeps to one.
internal sealed class IdentityProfileEditDialog : MultiChildView, IBind<IdentityProfileEditDialogViewModel>
{
    private readonly LabeledInputField _displayName;
    private readonly LabeledInputField _authorName;
    private readonly LabeledInputField _authorEmail;
    private readonly LabeledInputField _sshKey;
    private readonly LabeledInputField _matchHost;
    private readonly LabeledInputField _matchOwner;
    private readonly DialogShell _shell;
    private readonly Action _onClose;

    public IdentityProfileEditDialog(IdentityProfile? existing, Action onClose)
    {
        _onClose = onClose;

        _displayName = new LabeledInputField("Profile name") { Placeholder = "Work" };
        _authorName = new LabeledInputField("Author name") { Placeholder = "Jane Dev" };
        _authorEmail = new LabeledInputField("Author email") { Placeholder = "jane@company.com" };
        _sshKey = new LabeledInputField("SSH key (optional)")
        {
            Placeholder = "~/.ssh/id_work",
            Hint = "Used for fetch/push from repos matched by this profile.",
        };
        _matchHost = new LabeledInputField("Match: host")
        {
            Placeholder = "github.com",
            Hint = "Repos whose remote is on this host use this profile.",
        };
        _matchOwner = new LabeledInputField("Match: owner (optional)")
        {
            Placeholder = "your-org",
            Hint = "Limit to one org/user. Leave blank to match any repo on the host.",
        };

        var add = existing == null;
        _shell = new DialogShell(add ? "New identity" : "Edit identity", onClose)
        {
            Width = DialogFrame.WidthWide,
            Action = (add ? "Add" : "Save", DialogButtonRole.Primary),
            Body = { _displayName, _authorName, _authorEmail, _sshKey, _matchHost, _matchOwner },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(
            _displayName.Input, _authorName.Input, _authorEmail.Input,
            _sshKey.Input, _matchHost.Input, _matchOwner.Input);

        this.UseViewModel(
            ctx => new IdentityProfileEditDialogViewModel(
                existing,
                ctx.Require<IdentityProfileService>(),
                ctx.Require<IUiDispatcher>()),
            Bind);
    }

    public void Bind(IdentityProfileEditDialogViewModel vm)
    {
        _displayName.Input.BindTwoWay(vm.DisplayName);
        _authorName.Input.BindTwoWay(vm.AuthorName);
        _authorEmail.Input.BindTwoWay(vm.AuthorEmail);
        _sshKey.Input.BindTwoWay(vm.SshKeyPath);
        _matchHost.Input.BindTwoWay(vm.MatchHost);
        _matchOwner.Input.BindTwoWay(vm.MatchOwner);

        _shell.BindCommand(vm.Save);
        vm.CloseRequested += _onClose;

        _shell.BeginEditing();
    }
}
