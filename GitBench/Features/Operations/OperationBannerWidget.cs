using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// Thin status strip above the main content while the repo is mid-operation (merge / rebase /
/// cherry-pick / revert / bisect / am) or has unmerged paths from a stash-apply conflict. Reports
/// state only — the Continue / Skip / Abort actions live in <see cref="OperationPanelWidget"/> at
/// the bottom, both driven by the shared <see cref="OperationBannerViewModel"/>.
/// </summary>
internal sealed record OperationBannerWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationBannerViewModel>();
        return new Show
        {
            When = vm.IsActive,
            Then = () => Banner(vm),
        };
    }

    private static IWidget Banner(OperationBannerViewModel vm) => new Box
    {
        Background = Theme.Color(s => s.Banner.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border }),
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg, Top = Spacing.Sm, Bottom = Spacing.Sm },
                Children =
                [
                    new Text
                    {
                        Value = L.T(s => vm.IsBusy.Value
                            ? BusyMessageFor(s, vm.OperationState.Value)
                            : MessageFor(s, vm.OperationState.Value, vm.HasConflicts.Value)),
                        VAlign = TextAlignment.Center,
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(s => s.Banner.Text),
                    },
                ],
            },
        ],
    };

    private static string BusyMessageFor(Strings s, RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => s.OperationsBannerContinuingMerge,
        RepoOperationState.Rebase => s.OperationsBannerContinuingRebase,
        RepoOperationState.CherryPick => s.OperationsBannerContinuingCherryPick,
        RepoOperationState.Revert => s.OperationsBannerContinuingRevert,
        RepoOperationState.ApplyMailbox => s.OperationsBannerContinuingApply,
        _ => s.OperationsBannerBusyDefault,
    };

    private static string MessageFor(Strings s, RepoOperationState state, bool hasConflicts) => state switch
    {
        RepoOperationState.Merge => hasConflicts ? s.OperationsBannerMsgMerge : s.OperationsBannerMsgMergeResolved,
        RepoOperationState.Rebase => hasConflicts ? s.OperationsBannerMsgRebase : s.OperationsBannerMsgRebaseResolved,
        RepoOperationState.CherryPick => hasConflicts ? s.OperationsBannerMsgCherryPick : s.OperationsBannerMsgCherryPickResolved,
        RepoOperationState.Revert => hasConflicts ? s.OperationsBannerMsgRevert : s.OperationsBannerMsgRevertResolved,
        RepoOperationState.ApplyMailbox => hasConflicts ? s.OperationsBannerMsgApply : s.OperationsBannerMsgApplyResolved,
        RepoOperationState.Bisect => s.OperationsBannerMsgBisect,
        RepoOperationState.UnmergedPaths => s.OperationsBannerMsgUnmerged,
        _ => string.Empty,
    };
}
