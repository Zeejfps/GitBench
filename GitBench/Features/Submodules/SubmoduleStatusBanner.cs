using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Submodules;

/// <summary>
/// Repo-level banner shown whenever the active repo has submodules whose working tree no
/// longer matches the commit the parent records (modified or conflicted). This is the safety
/// net for the gap left when `git pull --recurse-submodules` can't reconcile a dirty/conflicted
/// submodule. Mounted alongside the detached-HEAD banner so it's visible on any tab.
/// Not dismissible — it clears once the submodules are updated.
/// </summary>
internal sealed record SubmoduleStatusBanner : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<SubmoduleStatusBannerViewModel>();
        var loc = ctx.Localization();

        return new Show
        {
            When = vm.IsOutdated,
            Then = () => Banner(vm, loc),
        }.BindVm(vm);
    }

    private static IWidget Banner(SubmoduleStatusBannerViewModel vm, ILocalizationService loc) => new Box
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
                    new Row
                    {
                        Gap = Spacing.Md,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Grow
                            {
                                Child = new Text
                                {
                                    Value = L.T(s => s.SubmodulesBannerOutdated(vm.OutdatedCount.Value)),
                                    VAlign = TextAlignment.Center,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Banner.Text),
                                },
                            },
                            new ButtonWidget
                            {
                                Style = ButtonStyle.Filled(s => s.Status.SuccessBar),
                                Command = vm.UpdateSubmodules,
                                Children =
                                [
                                    new ButtonIcon { Value = LucideIcons.Package },
                                    new ButtonLabel { Value = L.T(s => s.SubmodulesBannerAction) },
                                ],
                            }.WithTooltip(L.T(s => s.SubmodulesBannerActionTooltip))
                                .WithController<KbmController>(),
                        ],
                    },
                ],
            },
        ],
    };
}
