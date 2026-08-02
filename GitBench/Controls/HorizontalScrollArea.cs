using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

internal sealed record HorizontalScrollArea : Widget
{
    public required IWidget Child { get; init; }

    /// <summary>
    /// Lets a plain vertical wheel pan this area sideways when it has nothing horizontal to go on.
    /// Only for areas that own the wheel outright — a lone horizontal strip like the actions toolbar
    /// or the tab strip, where a mouse with no tilt wheel would otherwise never reach the overflow.
    /// Off by default: an area sitting inside a vertically scrolling page (a code block or table in
    /// the transcript) must leave the vertical wheel to the page, or hovering it wedges the scroll.
    /// </summary>
    public bool VerticalWheelPans { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var scroller = new HorizontalScrollView(Child.BuildView(ctx));
        var verticalWheelPans = VerticalWheelPans;
        return new KbmInput
        {
            Controller = _ => new HorizontalScrollWheelController(scroller, verticalWheelPans),
            Child = new Raw { View = scroller },
        };
    }
}

internal sealed class HorizontalScrollWheelController : KeyboardMouseController
{
    private readonly HorizontalScrollView _view;
    private readonly bool _verticalWheelPans;

    public HorizontalScrollWheelController(HorizontalScrollView view, bool verticalWheelPans = false)
    {
        _view = view;
        _verticalWheelPans = verticalWheelPans;
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;

        var delta = e.DeltaX;
        if (delta == 0f && _verticalWheelPans) delta = e.DeltaY;
        if (delta == 0f) return;

        // Consume only what actually moved us: an area that fits, or one already pinned against the
        // edge the wheel is pushing toward, has to let the event bubble to whatever scrolls behind it.
        if (_view.ScrollHorizontal(-delta * Scrolling.WheelStep))
            e.Consume();
    }
}
