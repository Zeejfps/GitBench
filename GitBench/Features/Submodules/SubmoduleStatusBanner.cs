using GitBench.Controls;
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

        return new Show
        {
            When = vm.IsOutdated,
            Then = () => Banner(vm),
        }.BindVm(vm);
    }

    private static IWidget Banner(SubmoduleStatusBannerViewModel vm) => new Box
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
                                    Value = vm.OutdatedCount.Bind(BannerText),
                                    VAlign = TextAlignment.Center,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Banner.Text),
                                },
                            },
                            new ActionButton
                            {
                                Tooltip = "Check submodules out to the commit the main repo records",
                                Style = ButtonStyle.Filled(0xFF4E8B3D),
                                Command = vm.UpdateSubmodules,
                                Children =
                                [
                                    new ButtonIcon { Value = LucideIcons.Package },
                                    new ButtonLabel { Value = "Update submodules" },
                                ],
                            }.WithController<KbmController>(),
                        ],
                    },
                ],
            },
        ],
    };

    private static string? BannerText(int n) => n == 1
        ? "1 submodule is out of date — its working tree differs from the commit the main repo records."
        : $"{n} submodules are out of date — their working trees differ from the commits the main repo records.";
}
