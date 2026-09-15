using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;

namespace GitBench.Controls;

/// <summary>
/// Turns wheel notches into scroll distance for a pane. Bubbling only, so the innermost scrollable
/// under the pointer goes first; and consuming only what moved, so a pane that fits or is pinned
/// at the edge the wheel pushes toward lets the event reach whatever scrolls behind it.
/// </summary>
internal sealed class WheelScrollController : KeyboardMouseController
{
    private readonly Func<float, float, bool> _scroll;

    /// <param name="scroll">Applies a (horizontal, vertical) pixel delta; returns whether the wheel
    /// is now spent, i.e. the event should not travel further.</param>
    public WheelScrollController(Func<float, float, bool> scroll)
    {
        _scroll = scroll;
    }

    public static WheelScrollController For(VerticalScrollPane pane) =>
        new((_, dy) => pane.Scroll(dy));

    public static WheelScrollController For(ScrollPane pane) =>
        new((dx, dy) => pane.ScrollVertical(dy) | pane.ScrollHorizontal(dx));

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (_scroll(-e.DeltaX * Scrolling.WheelStep, -e.DeltaY * Scrolling.WheelStep))
            e.Consume();
    }
}
