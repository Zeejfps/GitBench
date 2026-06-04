using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal shown when the user picks "Delete…" on a stash row. Running
/// `git stash drop` is destructive — the stash cannot be recovered from the UI
/// afterwards, so the action is gated behind this prompt.
/// </summary>
internal sealed class DeleteStashDialog : MultiChildView, IBind<DeleteStashViewModel>
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    public DeleteStashDialog(Repo repo, int index, string subject, Action onClose)
    {
        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"{subject}\n\nThis stash will be permanently deleted. This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell("Delete stash?", onClose)
        {
            Width = DialogFrame.WidthCompact,
            Action = ("Delete", DialogButtonRole.Destructive),
            Body = { prompt },
        };
        AddChildToSelf(_shell.View);

        this.UseViewModel(
            ctx => new DeleteStashViewModel(
                repo,
                index,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeleteStashViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _shell.BindCommand(vm.Delete);
    }
}
