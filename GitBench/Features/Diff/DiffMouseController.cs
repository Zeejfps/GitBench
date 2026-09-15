using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

internal sealed class DiffMouseController : KeyboardMouseController
{
    private readonly DiffRowSurface _surface;

    public DiffMouseController(DiffRowSurface surface) => _surface = surface;

    public override void OnMouseMoved(ref MouseMoveEvent e) => _surface.PointerMoved(e.Mouse.Point);

    public override void OnMouseExit(ref MouseExitEvent e) => _surface.ClearHover();

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        if (e.State != InputState.Pressed) return;
        if (e.Button != MouseButton.Left) return;
        if (_surface.Click(e.Mouse.Point, e.Modifiers)) e.Consume();
    }
}
