using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

/// <summary>
/// Repo-level detached-HEAD banner, mounted above both the branches sidebar and the main content
/// so it's visible regardless of the active tab. Two shapes: a warning ("Create branch") when the
/// commits sit on no branch and would be orphaned by a checkout, and — for a submodule the
/// superproject parked on a branch tip — an informational "Switch to &lt;branch&gt;" that attaches
/// it back onto that branch. The banner is swapped in by <see cref="Show"/> only while relevant,
/// so its views and bindings exist only when shown; the view model is owned by the swap host
/// (<see cref="WidgetExtensions.BindVm"/>) so it keeps listening and can re-show the banner. The
/// host collapses its layout slot when there's nothing to show. Not dismissible — it clears
/// automatically once HEAD lands on a branch.
/// </summary>
internal sealed record DetachedHeadBanner : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DetachedHeadBannerViewModel>();

        return new Show
        {
            When = vm.IsVisible,
            Then = () => Banner(vm),
        }.BindVm(vm);
    }

    private static IWidget Banner(DetachedHeadBannerViewModel vm) => new Box
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
                    new Show { When = vm.IsAtRisk, Then = () => AtRiskRow(vm) },
                    new Show { When = vm.IsOnBranchTip, Then = () => OnBranchTipRow(vm) },
                ],
            },
        ],
    };

    private static IWidget AtRiskRow(DetachedHeadBannerViewModel vm) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    Value = L.T(s => s.BranchesDetachedHeadMessage),
                    VAlign = TextAlignment.Center,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Banner.Text),
                },
            },
            new ButtonWidget
            {
                Style = ButtonStyle.Filled(s => s.Status.SuccessBar),
                Command = vm.CreateBranch,
                Children =
                [
                    new ButtonIcon { Value = LucideIcons.Branch },
                    new ButtonLabel { Value = L.T(s => s.BranchesDetachedHeadCreateButton) },
                ],
            }.WithTooltip(L.T(s => s.BranchesDetachedHeadTooltip))
                .WithController<KbmController>(),
        ],
    };

    private static IWidget OnBranchTipRow(DetachedHeadBannerViewModel vm) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    Value = Prop.Bind(vm.OnTipMessage),
                    VAlign = TextAlignment.Center,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Banner.Text),
                },
            },
            new ButtonWidget
            {
                Style = ButtonStyle.Filled(s => s.Status.SuccessBar),
                Command = vm.SwitchToBranch,
                Children =
                [
                    new ButtonIcon { Value = LucideIcons.Branch },
                    new ButtonLabel { Value = Prop.Bind(vm.SwitchLabel) },
                ],
            }.WithTooltip(Prop.Bind(vm.SwitchTooltip))
                .WithController<KbmController>(),
        ],
    };
}
