using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
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
        var primary = Primary;
        var targetPaths = Target is { } target ? new[] { SubmodulePaths.Relative(Primary.Path, target.Path) } : null;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitSubmoduleOperations>();
        var bus = ctx.Require<IMessageBus>();

        var init = new State<bool>(true);
        var recursive = new State<bool>(false);
        var mode = new State<SubmoduleUpdateMode>(SubmoduleUpdateMode.Checkout);

        var update = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            // A Conflicted outcome means the update did land — the Operation banner takes
            // over to resolve it, so close and refresh like a clean success rather than
            // surfacing it as an error.
            work: () => gitService.UpdateSubmodules(primary, new SubmoduleUpdateRequest(
                Paths: targetPaths,
                Init: init.Value,
                Recursive: recursive.Value,
                Mode: mode.Value)),
            onSuccess: () =>
            {
                onClose();
                bus.Broadcast(new SubmodulesChangedMessage(primary.Id));
                bus.Broadcast(new RefsChangedMessage(primary.Id));
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = Target is null ? s.SubmodulesUpdateTitleAll : s.SubmodulesUpdateTitleSingle,
            OnClose = onClose,
            BodyGap = 10,
            Action = (s.SubmodulesUpdateAction, DialogButtonRole.Primary),
            Command = update,
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
                new CheckboxWidget { Label = s.SubmodulesUpdateInitLabel, Checked = init, Height = Sizes.RowHeight }.WithController<KbmController>(),
                new CheckboxWidget { Label = s.SubmodulesUpdateRecursiveLabel, Checked = recursive, Height = Sizes.RowHeight }.WithController<KbmController>(),
                new Text
                {
                    Value = s.CommonStrategy,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                ModeCheckbox(ctx, mode, s.SubmodulesUpdateStrategyCheckout, SubmoduleUpdateMode.Checkout),
                ModeCheckbox(ctx, mode, s.SubmodulesUpdateStrategyMerge, SubmoduleUpdateMode.Merge),
                ModeCheckbox(ctx, mode, s.SubmodulesUpdateStrategyRebase, SubmoduleUpdateMode.Rebase),
                new Text
                {
                    Value = s.SubmodulesUpdateNote,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }

    private static IWidget ModeCheckbox(Context ctx, State<SubmoduleUpdateMode> chosen, string label, SubmoduleUpdateMode mode)
    {
        var selected = new State<bool>(chosen.Value == mode);
        var view = new CheckboxWidget { Label = label, Checked = selected, Height = Sizes.RowHeight }.WithController<KbmController>().BuildView(ctx);
        view.Bind(chosen, m => selected.Value = m == mode);
        selected.Changed += isCheckedNow =>
        {
            if (!isCheckedNow)
            {
                if (chosen.Value == mode)
                    selected.Value = true;
                return;
            }
            if (chosen.Value == mode) return;
            chosen.Value = mode;
        };
        return new Raw { View = view };
    }
}
