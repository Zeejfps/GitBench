using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class TooltipView : MultiChildView
{
    private const int HorizontalPadding = 8;
    private const int VerticalPadding = 4;
    private const float CornerRadius = 4f;

    public TooltipView(string text)
    {
        var label = new TextView
        {
            Text = text,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => s.Tooltip.Text);

        var box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(CornerRadius),
            Padding = new PaddingStyle
            {
                Left = HorizontalPadding,
                Right = HorizontalPadding,
                Top = VerticalPadding,
                Bottom = VerticalPadding,
            },
            Children = { label },
        };
        box.BindThemedBackgroundColor(s => s.Tooltip.Background);
        box.BindThemedBorderColor(s => BorderColorStyle.All(s.Tooltip.Border));
        box.BindThemed(s => box.BoxShadow = new BoxShadowStyle
        {
            OffsetX = 0f,
            OffsetY = -4f,
            Blur = 16f,
            Spread = 0f,
            Color = s.Tooltip.Shadow,
        });
        AddChildToSelf(box);
    }

    protected override void OnLayoutSelf()
    {
        var width = MeasureWidth();
        var height = MeasureHeight(width);
        Position = new RectF { Left = 0, Bottom = 0, Width = width, Height = height };
    }
}
