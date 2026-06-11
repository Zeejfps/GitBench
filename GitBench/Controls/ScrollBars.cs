using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.HorizontalScrollBar;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Controls;

internal static class ScrollBars
{
    public static VerticalScrollBarView CreateVertical()
    {
        return CreateVerticalBar(CompatUi.Input);
    }

    public static VerticalScrollBarView CreateVertical(Context ctx)
    {
        return CreateVerticalBar(ctx.Require<InputSystem>());
    }

    private static VerticalScrollBarView CreateVerticalBar(InputSystem input)
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
        WireVertical(bar, input);
        return bar;
    }

    private static void WireVertical(VerticalScrollBarView bar, InputSystem input)
    {
        var thumb = bar.Thumb;
        var hovered = false;
        DragRecognizer? drag = null;
        thumb.UseController(input, () => drag = new DragRecognizer(input)
        {
            DragStarted = () => thumb.IsSelected = true,
            Dragged = delta => thumb.Move(delta.Y),
            DragEnded = () =>
            {
                if (!hovered) thumb.IsSelected = false;
            },
        });
        thumb.UseController(input, new KbmHandlers
        {
            OnHoverEnter = () =>
            {
                hovered = true;
                thumb.IsSelected = true;
            },
            OnHoverExit = () =>
            {
                hovered = false;
                if (drag is not { IsDragging: true }) thumb.IsSelected = false;
            },
        });
        bar.UseController(input, new KbmHandlers
        {
            OnMouseButton = (ref MouseButtonEvent e) =>
            {
                if (e.Phase == EventPhase.Bubbling
                    && e.Button == MouseButton.Left
                    && e.State == InputState.Pressed)
                {
                    bar.ScrollToPoint(e.Mouse.Point);
                    e.Consume();
                }
            },
        });
    }

    public static HorizontalScrollBarView CreateHorizontal()
    {
        return CreateHorizontalBar(CompatUi.Input);
    }

    public static HorizontalScrollBarView CreateHorizontal(Context ctx)
    {
        return CreateHorizontalBar(ctx.Require<InputSystem>());
    }

    private static HorizontalScrollBarView CreateHorizontalBar(InputSystem input)
    {
        var bar = new HorizontalScrollBarView(input)
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
        bar.UseController(input, () => new HorizontalScrollBarViewController(bar));
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
