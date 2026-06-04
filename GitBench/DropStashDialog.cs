using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal shown after a stash is successfully applied. Lets the user
/// drop the stash (the natural finish of "pop") or keep it around for re-use.
/// Running `git stash drop` here is destructive — the stash cannot be recovered
/// from the UI afterwards.
/// </summary>
internal sealed class DropStashDialog : MultiChildView, IBind<DropStashViewModel>
{
    private readonly Action _onClose;
    private readonly DialogShell _shell;

    public DropStashDialog(Repo repo, int index, string label, string subject, Action onClose)
    {
        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Applied: {subject}\n\nDrop this stash now? This cannot be undone.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _shell = new DialogShell($"Drop {label}?", onClose)
        {
            Width = DialogFrame.WidthCompact,
            CancelLabel = "Keep",
            Action = ("Drop", DialogButtonRole.Destructive),
            Body = { prompt },
        };
        AddChildToSelf(_shell.View);

        this.UseViewModel(
            ctx => new DropStashViewModel(
                repo,
                index,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DropStashViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _shell.BindCommand(vm.Drop);
    }
}
