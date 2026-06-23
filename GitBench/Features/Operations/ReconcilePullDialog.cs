using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Shown when a pull fails because the local branch and its upstream have diverged and git
/// refuses to pick a reconcile strategy on its own ("Need to specify how to reconcile divergent
/// branches"). Lets the user choose merge / rebase / fast-forward-only and reruns the pull with
/// that flag, so the divergence is resolved in-app rather than dropping the raw git hint.
/// </summary>
internal sealed record ReconcilePullDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new ReconcilePullDialogViewModel(
            Repo,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = s.OperationsReconcileTitle,
            OnClose = OnClose,
            BodyGap = 10,
            Action = (s.CommonPull, DialogButtonRole.Primary),
            Command = vm.Pull,
            Error = vm.Error,
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.OperationsReconcileDesc(Repo.DisplayName),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.CommonStrategy,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                StrategyCheckbox(ctx, vm, s.OperationsReconcileStrategyMerge, PullStrategy.Merge),
                StrategyCheckbox(ctx, vm, s.OperationsReconcileStrategyRebase, PullStrategy.Rebase),
                StrategyCheckbox(ctx, vm, s.OperationsReconcileStrategyFfOnly, PullStrategy.FastForwardOnly),
                new Text
                {
                    Value = s.OperationsReconcileNote,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }

    private static IWidget StrategyCheckbox(Context ctx, ReconcilePullDialogViewModel vm, string label, PullStrategy strategy)
    {
        var selected = new State<bool>(vm.Strategy.Value == strategy);
        var view = new CheckboxWidget { Label = label, Checked = selected, Height = 22 }.WithController<KbmController>().BuildView(ctx);
        view.Bind(vm.Strategy, m => selected.Value = m == strategy);
        selected.Changed += isCheckedNow =>
        {
            if (!isCheckedNow)
            {
                if (vm.Strategy.Value == strategy)
                    selected.Value = true;
                return;
            }
            if (vm.Strategy.Value == strategy) return;
            vm.Strategy.Value = strategy;
        };
        return new Raw { View = view };
    }
}
