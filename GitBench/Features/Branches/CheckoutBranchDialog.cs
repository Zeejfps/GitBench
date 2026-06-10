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
/// Modal shown when the user double-clicks a remote branch that has no matching local
/// branch. Lets them pick the local branch name and whether to set up tracking, then
/// runs `git checkout -b &lt;local&gt; [--track|--no-track] &lt;remote&gt;/&lt;branch&gt;`.
/// </summary>
internal sealed class CheckoutBranchDialog : MultiChildView, IBind<CheckoutBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _nameField;
    private readonly CheckboxView _trackCheckbox;
    private readonly DialogShell _shell;

    public CheckoutBranchDialog(
        Repo repo,
        string remoteName,
        string remoteBranchName,
        string suggestedLocalName,
        Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Create a local branch from {remoteName}/{remoteBranchName}",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        _nameField = new LabeledInputField("Local branch name");

        _trackCheckbox = new CheckboxView("Track this remote branch")
        {
            Height = 22,
        };

        _shell = new DialogShell("Checkout branch", onClose)
        {
            Action = ("Checkout", DialogButtonRole.Primary),
            Body = { subtitle, _nameField, _trackCheckbox },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_nameField.Input);

        var request = new CheckoutRequest(repo, remoteName, remoteBranchName, suggestedLocalName);
        this.UseViewModel(
            ctx => new CheckoutBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(CheckoutBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _nameField.Input.BindTwoWay(vm.Name);
        _nameField.BindStatus(vm.NameStatus);
        _trackCheckbox.IsChecked.BindTwoWay(vm.Track);
        _shell.BindCommand(vm.Checkout);

        // Bind runs after the input is attached to a context — doing focus earlier (e.g.
        // in the dialog ctor) produced an empty-looking field, since StartEditing/StealFocus
        // hadn't run yet and caret/selection visuals were stale.
        _nameField.Input.SelectAll();
        _shell.BeginEditing();
    }
}
