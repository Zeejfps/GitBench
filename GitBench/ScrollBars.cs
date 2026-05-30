using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.HorizontalScrollBar;
using ZGF.Gui.VerticalScrollBar;

namespace GitGui;

internal static class ScrollBars
{
    public static VerticalScrollBarView CreateVertical()
    {
        var bar = new VerticalScrollBarView
        {
            TrackBorderSize = new BorderSizeStyle { Left = 1 },
        };
        bar.BindThemed(s =>
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
        StyleThumb(bar.Thumb);
        bar.UseController(_ => new VerticalScrollBarViewController(bar));
        return bar;
    }

    public static HorizontalScrollBarView CreateHorizontal()
    {
        var bar = new HorizontalScrollBarView
        {
            TrackBorderSize = new BorderSizeStyle { Top = 1 },
        };
        bar.BindThemed(s =>
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
        StyleThumb(bar.Thumb);
        bar.UseController(_ => new HorizontalScrollBarViewController(bar));
        return bar;
    }

    private static void StyleThumb(VerticalScrollBarThumbView thumb)
    {
        thumb.BorderSize = BorderSizeStyle.All(1);
        thumb.BindThemed(s =>
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
    }

    private static void StyleThumb(HorizontalScrollBarThumbView thumb)
    {
        thumb.BorderSize = BorderSizeStyle.All(1);
        thumb.BindThemed(s =>
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
    }
}
