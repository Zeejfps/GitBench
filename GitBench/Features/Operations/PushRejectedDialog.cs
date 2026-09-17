using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
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
/// Shown when a push is refused as non-fast-forward: someone pushed to the upstream since the
/// last fetch, so the local branch has to integrate their commits before it can land. Lets the
/// user pick rebase or merge, pulls with that strategy, then pushes again in the same step —
/// the way Fork recovers from the same rejection — instead of dropping the raw git hint. Only
/// reached when the divergence wasn't known before pushing; once fetched, the Push button offers
/// the force-push dialog instead.
/// </summary>
internal sealed record PushRejectedDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitRemoteOperations>();
        var bus = ctx.Require<IMessageBus>();
        var s = ctx.Localization().Strings.Value;

        var strategy = new State<PullStrategy>(PullStrategy.Rebase);
        var pullAndPush = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.Pull(repo, strategy.Value) switch
            {
                PullOutcome.Failed f => PushOutcome.Fail(f.Message),
                _ => gitService.Push(repo),
            },
            onSuccess: () =>
            {
                onClose();
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                bus.Broadcast(new RemoteSyncOptimisticMessage(repo.Id, Ahead: 0, Behind: 0));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(s.ToastPushed)));
            });

        return new Dialog
        {
            Title = s.OperationsPushRejectedTitle,
            OnClose = onClose,
            BodyGap = 10,
            Action = (s.OperationsPushRejectedAction, DialogButtonRole.Primary),
            Command = pullAndPush,
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.OperationsPushRejectedDesc(Repo.DisplayName) },
                new Text
                {
                    Value = s.CommonStrategy,
                    Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                },
                StrategyCheckbox(ctx, strategy, s.OperationsReconcileStrategyRebase, PullStrategy.Rebase),
                StrategyCheckbox(ctx, strategy, s.OperationsReconcileStrategyMerge, PullStrategy.Merge),
                new Text
                {
                    Value = s.OperationsPushRejectedNote,
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
