using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.StatusBar;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
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
    protected override View CreateView(Context ctx)
    {
        var updateService = ctx.Require<UpdateService>();
        var theme = ctx.Theme();

        var text = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindText(updateService.BannerMessage);
        text.BindTextColor(() => theme.Styles.Value.Banner.Text);

        var restartButton = new ActionButton
        {
            Icon = LucideIcons.Package,
            Label = "Restart",
            Tooltip = "Restart to finish updating",
            Background = 0xFF4E8B3D,
            Command = new Command(updateService.ApplyAndRestart),
        }.BuildView(ctx);

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = text },
                restartButton,
            },
        };

        var banner = new RectView
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = 12,
                Right = 12,
                Top = 6,
                Bottom = 6,
            },
            Children = { row },
        };
        banner.BindBackgroundColor(() => theme.Styles.Value.Banner.Background);
        banner.BindBorderColor(() => new BorderColorStyle { Bottom = theme.Styles.Value.Banner.Border });
        banner.BindIsVisible(updateService.BannerMessage, m => m != null);
        return banner;
    }
}
