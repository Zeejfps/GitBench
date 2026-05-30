using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;

namespace GitGui;

internal enum DragAxis { X, Y }

/// <summary>
/// Generic draggable-splitter controller. Captures the pointer on left-press, then on
/// each move event feeds the axis-aligned delta (X or Y in mouse coords) to
/// <paramref name="onDelta"/>. The container handles the meaning of that delta (resize a
/// fraction, change a width, etc.).
/// </summary>
internal sealed class SplitterController : KeyboardMouseController, IDisposable
{
    private readonly DragAxis _axis;
    private readonly Action<float> _onDelta;
    private readonly Action<bool> _onHoverChanged;
    private readonly InputSystem _inputSystem;

    private bool _dragging;
    private bool _hovered;
    private PointF _lastPoint;

    public SplitterController(Context context, DragAxis axis, Action<float> onDelta, Action<bool> onHoverChanged)
    {
        _axis = axis;
        _onDelta = onDelta;
        _onHoverChanged = onHoverChanged;
        _inputSystem = context.Get<InputSystem>()!;
    }

    public void Dispose()
    {
        if (_dragging)
        {
            _inputSystem.Blur(this);
            _dragging = false;
        }
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        _hovered = true;
        _onHoverChanged(true);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _hovered = false;
        // Keep the highlight up while a drag is in progress even if the cursor briefly
        // leaves the splitter rect.
        if (!_dragging) _onHoverChanged(false);
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            _dragging = true;
            _lastPoint = e.Mouse.Point;
            _inputSystem.StealFocus(this);
            e.Consume();
            return;
        }

        if (e.State == InputState.Released && _dragging)
        {
            _dragging = false;
            _inputSystem.Blur(this);
            if (!_hovered) _onHoverChanged(false);
            e.Consume();
        }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (!_dragging) return;
        var delta = e.Mouse.Point - _lastPoint;
        _lastPoint = e.Mouse.Point;
        var d = _axis == DragAxis.X ? delta.X : delta.Y;
        if (d != 0f) _onDelta(d);
        e.Consume();
    }

    public override void OnFocusLost()
    {
        if (_dragging)
        {
            _dragging = false;
            if (!_hovered) _onHoverChanged(false);
        }
    }
}
