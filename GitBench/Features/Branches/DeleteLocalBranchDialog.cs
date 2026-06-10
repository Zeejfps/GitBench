using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed class DeleteLocalBranchDialog : MultiChildView, IBind<DeleteLocalBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly CheckboxView _forceCheckbox;
    private readonly CheckboxView? _deleteRemoteCheckbox;
    private readonly DialogShell _shell;

    public DeleteLocalBranchDialog(Repo repo, string branchName, Action onClose)
        : this(repo, branchName, upstreamRemote: null, upstreamBranch: null, onClose) { }

    public DeleteLocalBranchDialog(
        Repo repo,
        string branchName,
        string? upstreamRemote,
        string? upstreamBranch,
        Action onClose)
    {
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

        _shell = new DialogShell("Delete branch", onClose)
        {
            Action = ("Delete", DialogButtonRole.Destructive),
            Body = { prompt, _forceCheckbox, hint },
        };
        if (_deleteRemoteCheckbox != null)
            _shell.Body.Add(_deleteRemoteCheckbox);
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

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

        _shell.BindCommand(vm.Delete);
    }
}
