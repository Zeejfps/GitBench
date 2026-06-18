using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Branches;

/// <summary>
/// Repo-level banner shown whenever the active repo is on a detached HEAD whose commits sit
/// on no branch — so they're reachable only from HEAD and would be orphaned by a checkout.
/// Mounted above both the branches sidebar and the main content so it's visible regardless
/// of the active tab. The banner is swapped in by <see cref="Show"/> only while at risk, so its
/// views and bindings exist only when shown; the view model is owned by the swap host
/// (<see cref="WidgetExtensions.BindVm"/>) so it keeps listening and can re-show the banner. The
/// host collapses its layout slot when there's nothing at risk. Not dismissible — it clears
/// automatically once the commits land on a branch.
/// </summary>
internal sealed record DetachedHeadBanner : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DetachedHeadBannerViewModel>();

        return new Show
        {
            When = vm.IsAtRisk,
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
                Amount = new PaddingStyle { Left = 12, Right = 12, Top = 6, Bottom = 6 },
                Children =
                [
                    new Row
                    {
                        Gap = 8,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Grow
                            {
                                Child = new Text
                                {
                                    Value = "Detached HEAD — your latest commits aren't on any branch.",
                                    VAlign = TextAlignment.Center,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Banner.Text),
                                },
                            },
                            new ButtonWidget
                            {
                                Style = ButtonStyle.Filled(0xFF4E8B3D),
                                Command = vm.CreateBranch,
                                Children =
                                [
                                    new ButtonIcon { Value = LucideIcons.Branch },
                                    new ButtonLabel { Value = "Create branch" },
                                ],
                            }.WithTooltip("Create a branch here so these commits aren't lost")
                                .WithController<KbmController>(),
                        ],
                    },
                ],
            },
        ],
    };
}
