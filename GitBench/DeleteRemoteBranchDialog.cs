using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for deleting a branch from a remote. Calls
/// `git push &lt;remote&gt; --delete &lt;branch&gt;` — a network operation that doesn't touch
/// local branches. The server may refuse for protected refs; that error is surfaced.
/// </summary>
internal sealed class DeleteRemoteBranchDialog : MultiChildView, IBind<DeleteRemoteBranchDialogViewModel>
{
    private readonly DialogButton _deleteButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;
    private readonly Action _onClose;

    public DeleteRemoteBranchDialog(Repo repo, string remoteName, string branchName, Action onClose)
    {
        Width = 480f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Delete '{branchName}' from remote '{remoteName}'?",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        var hint = DialogFrame.Hint(
            "This is a network operation. Your local branches are not affected.",
            TextWrap.Wrap);

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _deleteButton = new DialogButton("Delete") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Delete remote branch", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                hint,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _deleteButton),
            },
        }));

        this.UseController(_ => new DialogKbmController(_deleteButton.Command, onClose));

        var request = new DeleteRemoteBranchRequest(repo, remoteName, branchName);
        this.UseViewModel(
            ctx => new DeleteRemoteBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeleteRemoteBranchDialogViewModel vm)
    {
        _deleteButton.BindBusyCommand(vm.Delete);
        _cancelButton.DisableWhile(vm.Delete.IsRunning);
        _errorView.BindText(vm.Delete.Error, s => s ?? string.Empty);
        vm.CloseRequested += _onClose;
    }
}
