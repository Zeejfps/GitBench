using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Inline warning banner — bordered box with red-on-amber text. Self-managing: setting
/// <see cref="Message"/> to null toggles <see cref="View.IsVisible"/> off, so the bar is
/// skipped by layout (no residual gap in Flex/Column/Row containers).
/// </summary>
internal sealed class ErrorBarView : ContainerView
{
    public State<string?> Message { get; } = new(null);

    public ErrorBarView(Context ctx, int verticalPadding = 4)
    {
        var theme = ctx.Theme();
        this.BindIsVisible(Message, m => m != null);

        var text = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
        };
        text.BindText(Message);
        text.BindTextColor(() => theme.Styles.Value.Banner.Text);

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle
            {
                Left = 8,
                Right = 8,
                Top = verticalPadding,
                Bottom = verticalPadding,
            },
            Children = { text },
        };
        box.BindBackgroundColor(() => theme.Styles.Value.Banner.Background);
        box.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.Banner.Border));
        AddChildToSelf(box);
    }
}
