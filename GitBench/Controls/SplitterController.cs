using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Controls;

internal enum DragAxis { X, Y }

/// <summary>
/// Generic draggable-splitter controller. Captures the pointer on left-press, then on
/// each move event feeds the axis-aligned delta (X or Y in mouse coords) to
/// <paramref name="onDelta"/>. The container handles the meaning of that delta (resize a
/// fraction, change a width, etc.).
/// </summary>
internal sealed class SplitterController : KeyboardMouseController, IProvidesCursor, IDisposable
{
    private const int DoubleClickThresholdMs = 400;
    private const float ClickSlopPixels = 3f;

    private readonly DragAxis _axis;
    private readonly Action<float> _onDelta;
    private readonly Action<bool> _onHoverChanged;
    private readonly Action? _onDoubleClick;
    private readonly InputSystem _inputSystem;

    private bool _dragging;
    private bool _hovered;
    private PointF _lastPoint;
    private float _dragDistance;
    private int _lastClickTickMs;
    private bool _hasLastClick;

    public SplitterController(Context context, DragAxis axis, Action<float> onDelta, Action<bool> onHoverChanged,
        Action? onDoubleClick = null)
    {
        _axis = axis;
        _onDelta = onDelta;
        _onHoverChanged = onHoverChanged;
        _onDoubleClick = onDoubleClick;
        _inputSystem = context.Get<InputSystem>()!;
    }

    public MouseCursor Cursor =>
        _axis == DragAxis.X ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical;

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
            _dragDistance = 0f;
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
            DetectDoubleClick();
            e.Consume();
        }
    }

    // Double-click is detected by tick stamp like the row controllers (the event carries no click
    // count), and only a press released within the slop counts as a click — a quick pair of drags
    // must not trigger it.
    private void DetectDoubleClick()
    {
        if (_onDoubleClick == null) return;

        if (_dragDistance > ClickSlopPixels)
        {
            _hasLastClick = false;
            return;
        }

        var now = Environment.TickCount;
        if (_hasLastClick && unchecked(now - _lastClickTickMs) <= DoubleClickThresholdMs)
        {
            _hasLastClick = false;
            _onDoubleClick();
        }
        else
        {
            _lastClickTickMs = now;
            _hasLastClick = true;
        }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (!_dragging) return;
        var delta = e.Mouse.Point - _lastPoint;
        _lastPoint = e.Mouse.Point;
        var d = _axis == DragAxis.X ? delta.X : delta.Y;
        _dragDistance += MathF.Abs(d);
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
