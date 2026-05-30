using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitGui;

internal sealed class EmptyAreaClickController : KeyboardMouseController
{
    private readonly Action _onClick;

    public EmptyAreaClickController(Action onClick)
    {
        _onClick = onClick;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        // Bubble-only: row controllers consume the left-press in the capture pass,
        // so a click that reaches us here must have landed on empty space.
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left || e.State != InputState.Pressed) return;
        _onClick();
        e.Consume();
    }
}