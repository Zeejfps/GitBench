using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

/// <summary>
/// Thin status bar spanning the full width of the window bottom (the outer border layout's
/// South region). Currently just shows the running build version, right-aligned, so it's easy
/// to tell which release is in use and whether an update took effect.
/// </summary>
internal sealed class StatusBarView : MultiChildView
{
    private const int Height_ = 22;
    private const int HorizontalPadding = 8;

    public StatusBarView()
    {
        var version = new TextView
        {
            Text = $"v{AppVersion.Display}",
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
        };
        version.BindThemedTextColor(s => s.StatusBar.Text);

        var bar = new RectView
        {
            Height = Height_,
            BorderSize = new BorderSizeStyle { Top = 1 },
            Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
            Children =
            {
                new FlexRowView
                {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = { new FlexItem { Grow = 1, Child = version } },
                },
            },
        };
        bar.BindThemedBackgroundColor(s => s.StatusBar.Background);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Top = s.StatusBar.TopBorder });
        AddChildToSelf(bar);
    }
}
