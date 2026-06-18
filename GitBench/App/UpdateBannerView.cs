using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.StatusBar;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Banner shown above the main content when a downloaded update has been staged. Clicking
/// Restart relaunches into the new version. Self-managing: toggles <see cref="View.IsVisible"/>
/// off while no update is pending, so layout skips it (no residual gap) — same pattern as
/// <see cref="OperationBannerWidget"/> / <see cref="ErrorBarView"/>.
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
            Background = theme.Styles.Bind(s => s.Banner.Background),
            BorderColor = theme.Styles.Bind(s => new BorderColorStyle { Bottom = s.Banner.Border }),
            Visible = updateService.BannerMessage.Bind(m => m != null),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = 12,
                        Right = 12,
                        Top = 6,
                        Bottom = 6,
                    },
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
                                        VAlign = TextAlignment.Center,
                                        Wrap = TextWrap.Wrap,
                                        Value = updateService.BannerMessage,
                                        Color = Theme.Color(s => s.Banner.Text),
                                    },
                                },
                                new ActionButton
                                {
                                    Icon = LucideIcons.Package,
                                    Label = "Restart",
                                    Tooltip = "Restart to finish updating",
                                    Background = 0xFF4E8B3D,
                                    Command = new Command(updateService.ApplyAndRestart),
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
