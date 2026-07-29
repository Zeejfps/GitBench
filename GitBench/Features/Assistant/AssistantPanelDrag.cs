using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Assistant;

/// <summary>
/// The shared half of a panel drag: captures the pointer on left-press and reports each move as a
/// delta in the panel's own axes — inline (mirrored under RTL) and downward-positive.
/// </summary>
/// <remarks>
/// Acts on the bubbling phase only, so a button inside the grabbed strip answers for its own clicks
/// and only what it leaves reaches the drag.
/// </remarks>
internal abstract class AssistantPanelDragController : KeyboardMouseController, IDisposable
{
    private readonly InputSystem _input;
    private readonly View _view;

    private bool _dragging;
    private PointF _last;

    protected AssistantPanelDragController(InputSystem input, View view)
    {
        _input = input;
        _view = view;
    }

    protected bool IsRtl => _view.IsRtl;

    protected abstract void OnDrag(float dx, float dy);

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling || e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            _dragging = true;
            _last = e.Mouse.Point;
            _input.StealFocus(this);
            e.Consume();
            return;
        }

        if (e.State == InputState.Released && _dragging)
        {
            _dragging = false;
            _input.Blur(this);
            e.Consume();
        }
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (!_dragging || e.Phase != EventPhase.Bubbling) return;

        var delta = e.Mouse.Point - _last;
        _last = e.Mouse.Point;
        // Pointer Y runs up the window and X runs with the screen; the placement is measured from the
        // top leading corner, so both are converted here rather than in the model.
        if (delta.X != 0f || delta.Y != 0f)
            OnDrag(IsRtl ? -delta.X : delta.X, -delta.Y);
        e.Consume();
    }

    public override void OnFocusLost() => _dragging = false;

    public void Dispose()
    {
        if (!_dragging) return;
        _input.Blur(this);
        _dragging = false;
    }
}

/// <summary>Moves the panel. Attached to the header strip alone — the transcript below it scrolls and
/// selects text, and a drag-anywhere panel would fight both.</summary>
internal sealed class AssistantPanelMoveController : AssistantPanelDragController
{
    private readonly AssistantPanelPlacement _placement;

    public AssistantPanelMoveController(AssistantPanelPlacement placement, InputSystem input, View view)
        : base(input, view) =>
        _placement = placement;

    protected override void OnDrag(float dx, float dy) => _placement.Move(dx, dy);
}

/// <summary>Resizes the panel from one edge or corner, and asks for the pointer shape that says which.</summary>
internal sealed class AssistantPanelResizeController : AssistantPanelDragController, IProvidesCursor
{
    private readonly AssistantPanelPlacement _placement;
    private readonly AssistantGrip _grip;

    public AssistantPanelResizeController(
        AssistantPanelPlacement placement, InputSystem input, View view, AssistantGrip grip)
        : base(input, view)
    {
        _placement = placement;
        _grip = grip;
    }

    // The diagonals are named after the axis they run along on screen, so mirroring the layout swaps
    // which of the two a corner takes.
    public MouseCursor Cursor => _grip switch
    {
        AssistantGrip.Leading or AssistantGrip.Trailing => MouseCursor.ResizeHorizontal,
        AssistantGrip.Top or AssistantGrip.Bottom => MouseCursor.ResizeVertical,
        AssistantGrip.TopLeading or AssistantGrip.BottomTrailing =>
            IsRtl ? MouseCursor.ResizeNesw : MouseCursor.ResizeNwse,
        _ => IsRtl ? MouseCursor.ResizeNwse : MouseCursor.ResizeNesw,
    };

    protected override void OnDrag(float dx, float dy) => _placement.Resize(_grip, dx, dy);
}
