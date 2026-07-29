using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Controls;

/// <summary>
/// Makes a floating surface own the pointer across its own rectangle, so nothing behind it reacts to
/// a click, drag or wheel that landed on it.
/// </summary>
/// <remarks>
/// Hit-testing only sees views that carry a controller, so a panel drawn over the workspace is
/// otherwise transparent to input wherever it has no interactive child — the content underneath is
/// still the topmost thing the pointer can find. Attaching this to the surface itself puts it in the
/// hit test and ends the event there.
///
/// By default it consumes on the bubble only, which is what makes it safe on an ancestor: the
/// surface's own buttons, fields and scroll panes are dispatched first and take what belongs to them,
/// and this swallows the rest. A backdrop that must block a whole layer is the opposite case and
/// clears <see cref="BubblingOnly"/> to consume in both phases.
/// </remarks>
internal sealed class SurfacePointerBlocker : KeyboardMouseController
{
    public bool BubblingOnly { get; init; } = true;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (BubblingOnly && e.Phase != EventPhase.Bubbling) return;
        e.Consume();
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (BubblingOnly && e.Phase != EventPhase.Bubbling) return;
        e.Consume();
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (BubblingOnly && e.Phase != EventPhase.Bubbling) return;
        e.Consume();
    }
}
