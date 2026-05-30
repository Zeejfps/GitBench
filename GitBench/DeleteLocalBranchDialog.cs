using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class DeleteLocalBranchDialog : MultiChildView, IBind<DeleteLocalBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly CheckboxView _forceCheckbox;
    private readonly CheckboxView? _deleteRemoteCheckbox;
    private readonly DialogButton _cancelButton;
    private readonly DialogButton _deleteButton;
    private readonly TextView _errorView;

    public DeleteLocalBranchDialog(Repo repo, string branchName, Action onClose)
        : this(repo, branchName, upstreamRemote: null, upstreamBranch: null, onClose) { }

    public DeleteLocalBranchDialog(
        Repo repo,
        string branchName,
        string? upstreamRemote,
        string? upstreamBranch,
        Action onClose)
    {
        Width = 460f;

        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Delete local branch '{branchName}'?",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        var hint = DialogFrame.Hint(
            "Unchecked: refuses if the branch isn't fully merged into its upstream or HEAD.",
            TextWrap.Wrap);

        _forceCheckbox = new CheckboxView("Delete even if not merged")
        {
            Height = 22,
        };

        var hasUpstream = !string.IsNullOrEmpty(upstreamRemote) && !string.IsNullOrEmpty(upstreamBranch);
        if (hasUpstream)
        {
            _deleteRemoteCheckbox = new CheckboxView($"Also delete '{upstreamBranch}' on '{upstreamRemote}'")
            {
                Height = 22,
            };
        }

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _deleteButton = new DialogButton("Delete") { Height = DialogFrame.DefaultButtonHeight };

        var content = new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                _forceCheckbox,
                hint,
            },
        };
        if (_deleteRemoteCheckbox != null)
            content.Children.Add(_deleteRemoteCheckbox);
        content.Children.Add(_errorView);
        content.Children.Add(new MultiChildView { Height = 4 });
        content.Children.Add(DialogFrame.ButtonsRow(_cancelButton, _deleteButton));

        AddChildToSelf(DialogFrame.Build("Delete branch", onClose, content));

        this.UseController(_ => new DialogKbmController(_deleteButton.Command, onClose));

        var request = new DeleteLocalBranchRequest(repo, branchName, upstreamRemote, upstreamBranch);
        this.UseViewModel(
            ctx => new DeleteLocalBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeleteLocalBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        if (_deleteRemoteCheckbox != null)
            _deleteRemoteCheckbox.IsChecked.BindTwoWay(vm.DeleteRemote);

        _deleteButton.BindBusyCommand(vm.Delete);
        _cancelButton.DisableWhile(vm.Delete.IsRunning);
        _errorView.BindText(vm.Delete.Error, s => s ?? string.Empty);
    }
}
