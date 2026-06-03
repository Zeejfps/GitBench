using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

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
    private readonly CheckoutDialogKbmController _messageController;
    private readonly DialogButton _renameButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public RenameStashDialog(Repo repo, int index, string currentMessage, Action onClose)
    {
        _onClose = onClose;
        MinWidthConstraint = 420f;

        var subtitle = new TextView
        {
            Text = $"Renaming '{currentMessage}'",
            TextWrap = TextWrap.Wrap,
        };
        subtitle.BindThemedTextColor(s => s.DialogBody.BodyText);

        _messageField = new LabeledInputField("Description");

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _renameButton = new DialogButton("Rename") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Rename stash", onClose, new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                subtitle,
                _messageField,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _renameButton),
            },
        }));

        _messageController = new CheckoutDialogKbmController(_messageField.Input, _renameButton.Command, onClose);
        _messageField.Input.UseController(_ => _messageController);

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
        _renameButton.BindBusyCommand(vm.Rename);
        _cancelButton.DisableWhile(vm.Rename.IsRunning);
        _errorView.BindText(vm.Rename.Error, s => s ?? string.Empty);

        _messageField.Input.SelectAll();
        _messageController.BeginEditing();
    }
}
