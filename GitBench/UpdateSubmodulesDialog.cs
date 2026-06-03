using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Modal shown from "Update all submodules…" on a primary repo or "Update submodule…"
/// on an individual submodule row. Lets the user pick init / recursive flags plus an
/// update strategy (checkout / merge / rebase).
/// </summary>
internal sealed class UpdateSubmodulesDialog : MultiChildView, IBind<UpdateSubmodulesDialogViewModel>
{
    private readonly Action _onClose;
    private readonly CheckboxView _initCheckbox;
    private readonly CheckboxView _recursiveCheckbox;
    private readonly CheckboxView _checkoutMode;
    private readonly CheckboxView _mergeMode;
    private readonly CheckboxView _rebaseMode;
    private readonly DialogShell _shell;

    public UpdateSubmodulesDialog(Repo primary, Repo? target, Action onClose)
    {
        _onClose = onClose;

        var titleText = target is null ? "Update all submodules" : "Update submodule";

        var prompt = new TextView
        {
            Text = target is null
                ? $"Run `git submodule update` on every submodule under '{primary.DisplayName}'."
                : $"Run `git submodule update` on '{target.DisplayName}'.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        _initCheckbox = new CheckboxView("Init missing submodules (--init)") { Height = 22 };
        _recursiveCheckbox = new CheckboxView("Recurse into nested submodules (--recursive)") { Height = 22 };

        var modeLabel = DialogFrame.Label("Strategy");

        _checkoutMode = new CheckboxView("Checkout (default — reset to recorded SHA)") { Height = 22 };
        _mergeMode = new CheckboxView("Merge (--merge)") { Height = 22 };
        _rebaseMode = new CheckboxView("Rebase (--rebase)") { Height = 22 };

        var conflictsHint = DialogFrame.Hint(
            "Merge/rebase strategies may leave the submodule mid-merge on conflict — " +
            "the Operation banner will offer Abort.",
            TextWrap.Wrap);

        _shell = new DialogShell(titleText, onClose)
        {
            BodyGap = 10,
            Action = ("Update", DialogButtonRole.Primary),
            Body =
            {
                prompt,
                _initCheckbox,
                _recursiveCheckbox,
                modeLabel,
                _checkoutMode,
                _mergeMode,
                _rebaseMode,
                conflictsHint,
            },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        var request = new UpdateSubmodulesViewRequest(primary, target);
        this.UseViewModel(
            ctx => new UpdateSubmodulesDialogViewModel(
                request,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(UpdateSubmodulesDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _initCheckbox.IsChecked.BindTwoWay(vm.Init);
        _recursiveCheckbox.IsChecked.BindTwoWay(vm.Recursive);
        _shell.ActionButton.BindBusyCommand(vm.Update);
        _shell.CancelButton.DisableWhile(vm.Update.IsRunning);
        _shell.Error.BindText(vm.Error, s => s ?? string.Empty);

        vm.Mode.Subscribe(m =>
        {
            _checkoutMode.IsChecked.Value = m == SubmoduleUpdateMode.Checkout;
            _mergeMode.IsChecked.Value = m == SubmoduleUpdateMode.Merge;
            _rebaseMode.IsChecked.Value = m == SubmoduleUpdateMode.Rebase;
        });
        _checkoutMode.IsChecked.Changed += b => SelectMode(vm, SubmoduleUpdateMode.Checkout, b);
        _mergeMode.IsChecked.Changed += b => SelectMode(vm, SubmoduleUpdateMode.Merge, b);
        _rebaseMode.IsChecked.Changed += b => SelectMode(vm, SubmoduleUpdateMode.Rebase, b);
    }

    private void SelectMode(UpdateSubmodulesDialogViewModel vm, SubmoduleUpdateMode mode, bool isCheckedNow)
    {
        if (!isCheckedNow)
        {
            if (vm.Mode.Value == mode)
                CheckboxFor(mode).IsChecked.Value = true;
            return;
        }
        if (vm.Mode.Value == mode) return;
        vm.Mode.Value = mode;
    }

    private CheckboxView CheckboxFor(SubmoduleUpdateMode mode) => mode switch
    {
        SubmoduleUpdateMode.Merge => _mergeMode,
        SubmoduleUpdateMode.Rebase => _rebaseMode,
        _ => _checkoutMode,
    };
}
