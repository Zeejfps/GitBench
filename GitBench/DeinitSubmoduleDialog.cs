using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Confirmation modal for `git submodule deinit` + `git rm`. Refuses if the submodule
/// has uncommitted changes unless Force is checked (delegates the safety check to git).
/// </summary>
internal sealed class DeinitSubmoduleDialog : MultiChildView, IBind<DeinitSubmoduleDialogViewModel>
{
    private const float DialogWidth = 460f;

    private readonly Action _onClose;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogButton _deinitButton;
    private readonly DialogButton _cancelButton;
    private readonly TextView _errorView;

    public DeinitSubmoduleDialog(Repo primary, Repo submodule, Action onClose)
    {
        Width = DialogWidth;
        _onClose = onClose;

        var prompt = new TextView
        {
            Text = $"Deinit and remove submodule '{submodule.DisplayName}'?",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        var detail = DialogFrame.Hint(
            "Runs `git submodule deinit` followed by `git rm`. The submodule will " +
            "be removed from the working tree and the deletion staged in the parent " +
            "for your next commit.",
            TextWrap.Wrap);

        _forceCheckbox = new CheckboxView("Deinit even if dirty")
        {
            Height = 22,
        };

        _errorView = DialogFrame.ErrorView();

        _cancelButton = new DialogButton("Cancel", onClose) { Height = DialogFrame.DefaultButtonHeight };
        _deinitButton = new DialogButton("Deinit") { Height = DialogFrame.DefaultButtonHeight };

        AddChildToSelf(DialogFrame.Build("Deinit submodule", onClose, new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                prompt,
                detail,
                _forceCheckbox,
                _errorView,
                new MultiChildView { Height = 4 },
                DialogFrame.ButtonsRow(_cancelButton, _deinitButton),
            },
        }));

        this.UseController(_ => new DialogKbmController(_deinitButton.Command, onClose));

        var request = new DeinitSubmoduleViewRequest(primary, submodule);
        this.UseViewModel(
            ctx => new DeinitSubmoduleDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(DeinitSubmoduleDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;
        _forceCheckbox.IsChecked.BindTwoWay(vm.Force);
        _deinitButton.BindBusyCommand(vm.Deinit);
        _cancelButton.DisableWhile(vm.Deinit.IsRunning);
        _errorView.BindText(vm.Deinit.Error, s => s ?? string.Empty);
    }
}
