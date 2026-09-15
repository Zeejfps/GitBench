using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.HorizontalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Controls;

internal static class ScrollBars
{
    public static VerticalScrollBar CreateVertical(Context ctx) => new(ctx);

    public static HorizontalScrollBarView CreateHorizontal(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var bar = new HorizontalScrollBarView(input)
        {
            TrackBorderSize = new BorderSizeStyle { Top = 1 },
        };
        bar.BindThemed(ctx.Theme(), s =>
        {
            bar.TrackBackgroundColor = s.ScrollBar.TrackBackground;
            bar.TrackBorderColor = new BorderColorStyle
            {
                Left = s.ScrollBar.TrackBorder,
                Top = s.ScrollBar.TrackBorder,
                Right = s.ScrollBar.TrackBorder,
                Bottom = s.ScrollBar.TrackBorder,
            };
        });
        var thumb = bar.Thumb;
        thumb.BorderSize = BorderSizeStyle.All(1);
        thumb.BindThemed(ctx.Theme(), s =>
        {
            thumb.IdleBackgroundColor = s.ScrollBar.ThumbIdleBackground;
            thumb.HoveredBackgroundColor = s.ScrollBar.ThumbHoverBackground;
            thumb.BorderColor = new BorderColorStyle
            {
                Left = s.ScrollBar.ThumbBorder,
                Top = s.ScrollBar.ThumbBorder,
                Right = s.ScrollBar.ThumbBorder,
                Bottom = s.ScrollBar.ThumbBorder,
            };
        });
        bar.UseController(input, () => new HorizontalScrollBarViewController(bar));
        return bar;
    }
}
