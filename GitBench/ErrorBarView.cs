using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Inline warning banner — bordered box with red-on-amber text. Self-managing: setting
/// <see cref="Message"/> to null toggles <see cref="View.IsVisible"/> off, so the bar is
/// skipped by layout (no residual gap in Flex/Column/Row containers).
/// </summary>
internal sealed class ErrorBarView : MultiChildView
{
    public State<string?> Message { get; } = new(null);

    public ErrorBarView(int verticalPadding = 4)
    {
        this.BindIsVisible(Message, m => m != null);

        var text = new TextView
        {
            VerticalTextAlignment = TextAlignment.Center,
        };
        text.BindText(Message);
        text.BindThemedTextColor(s => s.Banner.Text);

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
        box.BindThemedBackgroundColor(s => s.Banner.Background);
        box.BindThemedBorderColor(s => BorderColorStyle.All(s.Banner.Border));
        AddChildToSelf(box);
    }
}
