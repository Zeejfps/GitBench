using GitBench.Controls;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// Banner shown above the main content while the repo is mid-operation (merge / rebase /
/// cherry-pick / revert / bisect / am) or has unmerged paths from a stash-apply conflict.
/// Offers Continue where the operation supports it, and Abort.
/// </summary>
internal sealed record OperationBannerWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationBannerViewModel>();
        var loc = ctx.Localization();

        return new Show
        {
            When = vm.IsActive,
            Then = () => Banner(vm, loc),
        }.BindVm(vm);
    }

    private static IWidget Banner(OperationBannerViewModel vm, ILocalizationService loc) => new Box
    {
        Background = Theme.Color(s => s.Banner.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border }),
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 12, Right = 12, Top = 6, Bottom = 6 },
                Children =
                [
                    new Row
                    {
                        Gap = 4,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Grow
                            {
                                Child = new Text
                                {
                                    Value = L.T(s => vm.IsBusy.Value
                                        ? BusyMessageFor(s, vm.OperationState.Value)
                                        : MessageFor(s, vm.OperationState.Value)),
                                    VAlign = TextAlignment.Center,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Banner.Text),
                                },
                            },
                            new Show
                            {
                                When = vm.IsBusy,
                                Then = () => Spinner(vm),
                                Else = () => Actions(vm),
                            },
                        ],
                    },
                ],
            },
        ],
    };

    // Spinner existence is gated on the memoized IsBusy bool, so it survives every busy frame;
    // the per-frame rotation flows in as a Prop, updating the live view without rebuilding it.
    private static IWidget Spinner(OperationBannerViewModel vm) => new Text
    {
        Value = LucideIcons.Loader,
        FontFamily = LucideIcons.FontFamily,
        FontSize = 16,
        VAlign = TextAlignment.Center,
        HAlign = TextAlignment.Center,
        Width = 20,
        Color = Theme.Color(s => s.Banner.Text),
        Rotation = Prop.Bind(vm.BusyRotation),
    };

    private static IWidget Actions(OperationBannerViewModel vm)
    {
        var continueStyle = ButtonStyle.Filled(0xFF4E8B3D);
        var abortStyle = ButtonStyle.Filled(0xFFB3514B);
        return new Row
        {
            Gap = 4,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new ButtonWidget
                {
                    Style = continueStyle,
                    ContentInset = continueStyle.IconOnlyInset,
                    Command = vm.Continue,
                    Visible = Prop.Bind(() => SupportsContinue(vm.OperationState.Value)),
                    Children = [new ButtonIcon { Value = LucideIcons.ChevronsRight }],
                }.WithTooltip(L.T(s => s.CommonContinue))
                    .WithController<KbmController>(),

                new ButtonWidget
                {
                    Style = abortStyle,
                    ContentInset = abortStyle.IconOnlyInset,
                    Command = vm.Abort,
                    Children = [new ButtonIcon { Value = LucideIcons.X }],
                }.WithTooltip(L.T(s => s.CommonAbort))
                    .WithController<KbmController>(),
            ],
        };
    }

    private static bool SupportsContinue(RepoOperationState state) => state switch
    {
        // Merge finishes through the commit box (commit creates the merge commit), so no
        // Continue button — unlike rebase/cherry-pick/revert, which advance via --continue.
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        _ => false,
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

    private static string MessageFor(Strings s, RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => s.OperationsBannerMsgMerge,
        RepoOperationState.Rebase => s.OperationsBannerMsgRebase,
        RepoOperationState.CherryPick => s.OperationsBannerMsgCherryPick,
        RepoOperationState.Revert => s.OperationsBannerMsgRevert,
        RepoOperationState.ApplyMailbox => s.OperationsBannerMsgApply,
        RepoOperationState.Bisect => s.OperationsBannerMsgBisect,
        RepoOperationState.UnmergedPaths => s.OperationsBannerMsgUnmerged,
        _ => string.Empty,
    };
}
