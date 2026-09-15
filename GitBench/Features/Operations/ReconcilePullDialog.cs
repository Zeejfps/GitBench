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
        var repo = Repo;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitRemoteOperations>();
        var bus = ctx.Require<IMessageBus>();

        var strategy = new State<PullStrategy>(PullStrategy.Merge);
        var pull = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.Pull(repo, strategy.Value),
            onSuccess: () =>
            {
                onClose();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
            });

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = s.OperationsReconcileTitle,
            OnClose = onClose,
            BodyGap = 10,
            Action = (s.CommonPull, DialogButtonRole.Primary),
            Command = pull,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.OperationsReconcileDesc(Repo.DisplayName) },
                new Text
                {
                    Value = s.CommonStrategy,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                StrategyCheckbox(ctx, strategy, s.OperationsReconcileStrategyMerge, PullStrategy.Merge),
                StrategyCheckbox(ctx, strategy, s.OperationsReconcileStrategyRebase, PullStrategy.Rebase),
                StrategyCheckbox(ctx, strategy, s.OperationsReconcileStrategyFfOnly, PullStrategy.FastForwardOnly),
                new Text
                {
                    Value = s.OperationsReconcileNote,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
            ],
        };
    }

    private static IWidget StrategyCheckbox(Context ctx, State<PullStrategy> chosen, string label, PullStrategy strategy)
    {
        var selected = new State<bool>(chosen.Value == strategy);
        var view = new CheckboxWidget { Label = label, Checked = selected, Height = Sizes.RowHeight }.WithController<KbmController>().BuildView(ctx);
        view.Bind(chosen, m => selected.Value = m == strategy);
        selected.Changed += isCheckedNow =>
        {
            if (!isCheckedNow)
            {
                if (chosen.Value == strategy)
                    selected.Value = true;
                return;
            }
            if (chosen.Value == strategy) return;
            chosen.Value = strategy;
        };
        return new Raw { View = view };
    }
}
