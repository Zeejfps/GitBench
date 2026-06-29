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

    protected override IWidget Build(Context ctx)
    {
        var scroller = new HorizontalScrollView(Child.BuildView(ctx));
        return new KbmInput
        {
            Controller = _ => new HorizontalScrollWheelController(scroller),
            Child = new Raw { View = scroller },
        };
    }
}

internal sealed class HorizontalScrollWheelController : KeyboardMouseController
{
    private readonly HorizontalScrollView _view;

    public HorizontalScrollWheelController(HorizontalScrollView view) => _view = view;

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        var delta = e.DeltaX != 0f ? e.DeltaX : e.DeltaY;
        if (delta == 0f) return;
        _view.ScrollHorizontal(-delta * Scrolling.WheelStep);
        e.Consume();
    }
}
