using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
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

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = Target is null ? s.SubmodulesUpdateTitleAll : s.SubmodulesUpdateTitleSingle,
            OnClose = OnClose,
            ViewModel = vm,
            BodyGap = 10,
            Action = (s.SubmodulesUpdateAction, DialogButtonRole.Primary),
            Command = vm.Update,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = Target is null
                        ? s.SubmodulesUpdateDescAll(Primary.DisplayName)
                        : s.SubmodulesUpdateDescSingle(Target.DisplayName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new CheckboxWidget { Label = s.SubmodulesUpdateInitLabel, Checked = vm.Init, Height = Sizes.RowHeight }.WithController<KbmController>(),
                new CheckboxWidget { Label = s.SubmodulesUpdateRecursiveLabel, Checked = vm.Recursive, Height = Sizes.RowHeight }.WithController<KbmController>(),
                new Text
                {
                    Value = s.CommonStrategy,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                ModeCheckbox(ctx, vm, s.SubmodulesUpdateStrategyCheckout, SubmoduleUpdateMode.Checkout),
                ModeCheckbox(ctx, vm, s.SubmodulesUpdateStrategyMerge, SubmoduleUpdateMode.Merge),
                ModeCheckbox(ctx, vm, s.SubmodulesUpdateStrategyRebase, SubmoduleUpdateMode.Rebase),
                new Text
                {
                    Value = s.SubmodulesUpdateNote,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }

    private static IWidget ModeCheckbox(Context ctx, UpdateSubmodulesDialogViewModel vm, string label, SubmoduleUpdateMode mode)
    {
        var selected = new State<bool>(vm.Mode.Value == mode);
        var view = new CheckboxWidget { Label = label, Checked = selected, Height = Sizes.RowHeight }.WithController<KbmController>().BuildView(ctx);
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
