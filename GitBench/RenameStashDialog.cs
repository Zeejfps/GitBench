using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Modal shown when the user picks "Rename…" on a stash row. Edits the stash's
/// description. git has no native stash rename, so <see cref="IGitService.RenameStash"/>
/// drops the entry and re-stores it under the new message — which moves the renamed
/// stash to the top of the list (stash@{0}).
/// </summary>
internal sealed class RenameStashDialog : MultiChildView, IBind<RenameStashDialogViewModel>
{
    private readonly Action _onClose;
    private readonly LabeledInputField _messageField;
    private readonly DialogShell _shell;

    public RenameStashDialog(Repo repo, int index, string currentMessage, Action onClose)
    {
        _onClose = onClose;

        var subtitle = new TextView
        {
            Text = $"Renaming '{currentMessage}'",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        _messageField = new LabeledInputField("Description");

        _shell = new DialogShell("Rename stash", onClose)
        {
            Action = ("Rename", DialogButtonRole.Primary),
            Body = { subtitle, _messageField },
        };
        AddChildToSelf(_shell.View);
        _shell.SubmitFrom(_messageField.Input);

        var request = new RenameStashRequest(repo, index, currentMessage);
        this.UseViewModel(
            ctx => new RenameStashDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(RenameStashDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _messageField.Input.BindTwoWay(vm.Message);
        _shell.BindCommand(vm.Rename);

        _messageField.Input.SelectAll();
        _shell.BeginEditing();
    }
}
