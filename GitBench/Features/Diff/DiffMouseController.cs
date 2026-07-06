using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Diff;

internal sealed class DiffMouseController : KeyboardMouseController
{
    private readonly DiffContentView _content;

    public DiffMouseController(DiffContentView content)
    {
        _content = content;
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        _content.OnHunkPointerMove(e.Mouse.Point);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _content.OnHunkPointerExit();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        if (e.State != InputState.Pressed) return;
        if (e.Button != MouseButton.Left) return;
        if (_content.TryClickExpander(e.Mouse.Point) || _content.TryClickHunkAction(e.Mouse.Point))
            e.Consume();
    }
}
