using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

/// <summary>
/// Modal shown from "Update all submodules…" on a primary repo or "Update submodule…"
/// on an individual submodule row. Lets the user pick init / recursive flags plus an
/// update strategy (checkout / merge / rebase).
/// </summary>
internal sealed record UpdateSubmodulesDialog : Widget
{
    public required Repo Primary { get; init; }
    public required Repo? Target { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new UpdateSubmodulesDialogViewModel(
            new UpdateSubmodulesViewRequest(Primary, Target),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        return new Dialog
        {
            Title = Target is null ? "Update all submodules" : "Update submodule",
            OnClose = OnClose,
            ViewModel = vm,
            BodyGap = 10,
            Action = ("Update", DialogButtonRole.Primary),
            Command = vm.Update,
            Error = vm.Error,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = Target is null
                        ? $"Run `git submodule update` on every submodule under '{Primary.DisplayName}'."
                        : $"Run `git submodule update` on '{Target.DisplayName}'.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Checkbox { Label = "Init missing submodules (--init)", Value = vm.Init, Height = 22 }.WithController<KbmController>(),
                new Checkbox { Label = "Recurse into nested submodules (--recursive)", Value = vm.Recursive, Height = 22 }.WithController<KbmController>(),
                new Text
                {
                    Value = "Strategy",
                    Color = Theme.Color(s => s.DialogBody.SectionHeaderText),
                },
                ModeCheckbox(ctx, vm, "Checkout (default — reset to recorded SHA)", SubmoduleUpdateMode.Checkout),
                ModeCheckbox(ctx, vm, "Merge (--merge)", SubmoduleUpdateMode.Merge),
                ModeCheckbox(ctx, vm, "Rebase (--rebase)", SubmoduleUpdateMode.Rebase),
                new Text
                {
                    Value = "Merge/rebase strategies may leave the submodule mid-merge on conflict — " +
                            "the Operation banner will offer Abort.",
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
            ],
        };
    }

    private static IWidget ModeCheckbox(Context ctx, UpdateSubmodulesDialogViewModel vm, string label, SubmoduleUpdateMode mode)
    {
        var selected = new State<bool>(vm.Mode.Value == mode);
        var view = new Checkbox { Label = label, Value = selected, Height = 22 }.WithController<KbmController>().BuildView(ctx);
        view.Bind(vm.Mode, m => selected.Value = m == mode);
        selected.Changed += isCheckedNow =>
        {
            if (!isCheckedNow)
            {
                if (vm.Mode.Value == mode)
                    selected.Value = true;
                return;
            }
            if (vm.Mode.Value == mode) return;
            vm.Mode.Value = mode;
        };
        return new Raw { View = view };
    }
}
