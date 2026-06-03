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
    private readonly Action _onClose;
    private readonly CheckboxView _forceCheckbox;
    private readonly DialogShell _shell;

    public DeinitSubmoduleDialog(Repo primary, Repo submodule, Action onClose)
    {
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

        _shell = new DialogShell("Deinit submodule", onClose)
        {
            Action = ("Deinit", DialogButtonRole.Destructive),
            Body = { prompt, detail, _forceCheckbox },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

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
        _shell.BindCommand(vm.Deinit);
    }
}
