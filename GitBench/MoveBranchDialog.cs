using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for "Reset &lt;branch&gt; to here" when the move is NOT a fast-forward —
/// the branch has commits that aren't in the selected revision, so force-moving it would
/// leave those commits unreachable. The fast-forward case skips this dialog (applied directly
/// by <see cref="CommitsViewModel"/>). Runs `git checkout -B &lt;branch&gt; &lt;sha&gt;` on confirm.
/// </summary>
internal sealed class MoveBranchDialog : MultiChildView, IBind<MoveBranchDialogViewModel>
{
    private readonly Action _onClose;
    private readonly DialogButton _resetButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public MoveBranchDialog(Repo repo, string branchName, string sha, string shortSha, string summary, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Reset '{branchName}' to the selected revision and check it out",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        var commitLine = new TextView
        {
            Text = string.IsNullOrEmpty(summary) ? shortSha : $"{shortSha}  {summary}",
            TextWrap = TextWrap.NoWrap,
        };
        commitLine.BindThemedTextColor(s => s.DialogBody.BodyText);
        var commitClip = new ClippingView { Children = { commitLine } };

        var warning = DialogFrame.Hint(
            $"'{branchName}' has commits that aren't in {shortSha}. Resetting it here will leave those commits unreachable from any branch.",
            TextWrap.Wrap);

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _resetButton = new DialogButton("Reset branch", role: DialogButtonRole.Destructive) { Height = DialogFrame.DefaultButtonHeight };

        var buttonsRow = DialogFrame.ButtonsRow(_cancelButton, _resetButton);

        AddChildToSelf(DialogFrame.Build("Reset branch to revision", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                commitClip,
                warning,
                _errorView,
                new MultiChildView { Height = 4 },
                buttonsRow,
            },
        }, DialogFrame.WidthWide));

        this.UseController(_ => new DialogKbmController(_resetButton.Command, onClose));

        var request = new MoveBranchRequest(repo, branchName, sha);
        this.UseViewModel(
            ctx => new MoveBranchDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(MoveBranchDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _resetButton.BindBusyCommand(vm.Move);
        _cancelButton.DisableWhile(vm.Move.IsRunning);
        _errorView.BindText(vm.Move.Error, s => s ?? string.Empty);
    }
}
