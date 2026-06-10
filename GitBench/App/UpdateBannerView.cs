using GitBench.Controls;
using GitBench.Features.Operations;
using GitBench.Features.StatusBar;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Banner shown above the main content when a downloaded update has been staged. Clicking
/// Restart relaunches into the new version. Self-managing: toggles <see cref="View.IsVisible"/>
/// off while no update is pending, so layout skips it (no residual gap) — same pattern as
/// <see cref="OperationBannerView"/> / <see cref="ErrorBarView"/>.
/// </summary>
internal sealed class UpdateBannerView : MultiChildView
{
    public UpdateBannerView(UpdateService updateService)
    {
        this.BindIsVisible(updateService.BannerMessage, m => m != null);

        var text = new TextView
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindText(updateService.BannerMessage);
        text.BindThemedTextColor(s => s.Banner.Text);

        var restartButton = new ActionButton(
            LucideIcons.Package,
            label: "Restart",
            tooltip: "Restart to finish updating",
            backgroundColor: 0xFF4E8B3D);
        restartButton.BindCommand(new Command(updateService.ApplyAndRestart));

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
        banner.BindThemedBackgroundColor(s => s.Banner.Background);
        banner.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border });
        AddChildToSelf(banner);
    }
}
