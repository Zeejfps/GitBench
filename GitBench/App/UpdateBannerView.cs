using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.StatusBar;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Banner shown above the main content when a downloaded update has been staged. Clicking
/// Restart relaunches into the new version. Self-managing: toggles <see cref="View.IsVisible"/>
/// off while no update is pending, so layout skips it (no residual gap) — same pattern as
/// <see cref="OperationBannerView"/> / <see cref="ErrorBarView"/>.
/// </summary>
internal sealed record UpdateBannerView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var updateService = ctx.Require<UpdateService>();
        var theme = ctx.Theme();

        return new Box
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = 12,
                Right = 12,
                Top = 6,
                Bottom = 6,
            },
            BindBackground = () => theme.Styles.Value.Banner.Background,
            BindBorder = () => new BorderColorStyle { Bottom = theme.Styles.Value.Banner.Border },
            BindVisible = () => updateService.BannerMessage.Value != null,
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
                            Child = new ThemedText
                            {
                                VAlign = TextAlignment.Center,
                                Wrap = TextWrap.Wrap,
                                Bind = () => updateService.BannerMessage.Value,
                                Color = s => s.Banner.Text,
                            },
                        },
                        new ActionButton
                        {
                            Icon = LucideIcons.Package,
                            Label = "Restart",
                            Tooltip = "Restart to finish updating",
                            Background = 0xFF4E8B3D,
                            Command = new Command(updateService.ApplyAndRestart),
                        },
                    ],
                },
            ],
        };
    }
}
