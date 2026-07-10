using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;

namespace GitBench.Features.Diff;

/// <summary>
/// What <see cref="DiffSelectionController"/> needs from a diff surface: the selection it owns,
/// its scrolling viewport, and a way to turn points into text positions. Implemented by
/// <see cref="DiffContentView"/> (one file, null scope) and the review list (one scope per file
/// card), which differ only in geometry.
/// </summary>
internal interface IDiffSelectionSurface
{
    DiffSelectionModel Selection { get; }

    /// <summary>The scrolling viewport, in GUI coordinates. Drives drag auto-scroll.</summary>
    RectF SelectionViewport { get; }

    /// <summary>The text position under a point, or null when the point isn't over a code line.</summary>
    DiffTextHit? HitTestText(PointF point);

    /// <summary>
    /// The nearest text position within <paramref name="scope"/>, for a drag that has wandered off
    /// its rows — past the viewport edge, into the gutter, onto another file's card. Never null
    /// while the scope still exists, so a drag keeps tracking wherever the cursor goes. A null
    /// scope resolves whichever scope the point lands in, which is how Select All finds its file.
    /// </summary>
    DiffTextHit? ClampToScope(PointF point, object? scope);

    /// <summary>True where a click belongs to something else: a hunk button, a gap expander, a
    /// card header. A press there neither starts a selection nor clears one.</summary>
    bool IsInteractiveAt(PointF point);

    /// <summary>The rows of a scope, for building clipboard text and Select All spans.</summary>
    IReadOnlyList<DiffRow>? RowsOf(object? scope);

    /// <summary>Scrolls the viewport by a delta in content pixels (positive scrolls down).</summary>
    void ScrollBy(float dy);

    void RequestRedraw();
}

/// <summary>
/// Drag-to-select, double/triple-click, and Ctrl/Cmd+C over a diff body.
///
/// Presses are watched in the capture phase but deliberately left unconsumed: the click still has
/// to reach the surface underneath (fold a card, open a gap). A selection only begins once the
/// pointer travels past <see cref="DragThreshold"/> with the button down, at which point the
/// controller takes focus and consumes moves so the drag keeps tracking outside the view — the
/// same bargain <see cref="DragRecognizer"/> strikes. Keyboard actions are hover-scoped, matching
/// the diff's other hotkeys: they fire while the pointer is over the body (or mid-drag) and fall
/// through otherwise, so Ctrl+A over the file list still selects files.
/// </summary>
internal sealed class DiffSelectionController : KeyboardMouseController, IProvidesCursor
{
    private const float DragThreshold = 3f;
    private const int MultiClickThresholdMs = 400;
    private const float MultiClickSlopPx = 4f;
    // Per-frame auto-scroll while the pointer is dragged past an edge, ramped by how far past.
    private const float AutoScrollMaxPerFrame = 24f;

    private readonly IDiffSelectionSurface _surface;
    private readonly InputSystem _input;
    private readonly IClipboard? _clipboard;

    private bool _pointerInside;
    // A press landed on text; a selection starts if the pointer travels before release.
    private bool _armed;
    private bool _dragging;
    private object? _dragScope;
    private PointF _pressPoint;
    private PointF _lastPoint;

    private int _clickCount;
    private int _lastClickTickMs;
    private PointF _lastClickPoint;
    private bool _hasLastClick;

    public DiffSelectionController(IDiffSelectionSurface surface, InputSystem input, IClipboard? clipboard)
    {
        _surface = surface;
        _input = input;
        _clipboard = clipboard;
    }

    // Read only while this controller captures the pointer, i.e. mid-drag. Hover cursors come
    // from the row list's own CursorAt hook.
    public MouseCursor Cursor => MouseCursor.Text;

    /// <summary>
    /// Called once per frame from the surface's draw. Scrolls the viewport while a drag hangs past
    /// an edge and extends the selection to keep up — moves alone can't do it, since a pointer
    /// held still outside the view emits none.
    /// </summary>
    public void Tick()
    {
        if (!_dragging) return;
        var viewport = _surface.SelectionViewport;
        if (viewport.Height <= 0) return;

        // GUI coordinates are y-up: above the viewport is a larger y.
        float dy;
        if (_lastPoint.Y > viewport.Top) dy = -Math.Min(AutoScrollMaxPerFrame, _lastPoint.Y - viewport.Top);
        else if (_lastPoint.Y < viewport.Bottom) dy = Math.Min(AutoScrollMaxPerFrame, viewport.Bottom - _lastPoint.Y);
        else return;

        _surface.ScrollBy(dy);
        ExtendTo(_lastPoint);
        _surface.RequestRedraw();
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (e.Phase == EventPhase.Capturing) _pointerInside = true;
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        if (e.Phase == EventPhase.Capturing) _pointerInside = false;
    }

    public override void OnFocusLost()
    {
        _armed = false;
        _dragging = false;
        if (_surface.Selection.Clear()) _surface.RequestRedraw();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Released)
        {
            _armed = false;
            if (!_dragging) return;
            _dragging = false;
            e.Consume();
            return;
        }

        // While focused this controller sees every press first, wherever it lands. One landing
        // outside the diff belongs to another surface: drop focus, which clears the selection.
        if (e.Phase == EventPhase.Bubbling)
        {
            if (!_surface.SelectionViewport.ContainsPoint(e.Mouse.Point)) _input.Blur(this);
            return;
        }

        if (e.Phase == EventPhase.Capturing) OnPress(ref e);
    }

    private void OnPress(ref MouseButtonEvent e)
    {
        var point = e.Mouse.Point;
        if (_surface.IsInteractiveAt(point))
        {
            _hasLastClick = false;
            return;
        }

        if (_surface.HitTestText(point) is not { } hit)
        {
            if (_surface.Selection.Clear()) _surface.RequestRedraw();
            _hasLastClick = false;
            return;
        }

        // The diff is now the focused surface, so Ctrl+C and Ctrl+A reach us rather than whatever
        // list held focus. Hovering that list still routes its own keys to it.
        _input.StealFocus(this);

        var rows = _surface.RowsOf(hit.Scope);
        var clicks = CountClick(point);

        if (rows != null && clicks >= 2)
        {
            var (start, end) = clicks >= 3
                ? DiffSelectionModel.LineSpan(rows, hit.Pos)
                : DiffSelectionModel.WordSpan(rows, hit.Pos);
            if (_surface.Selection.SetRange(hit.Scope, start, end)) _surface.RequestRedraw();
            e.Consume();
            return;
        }

        var selection = _surface.Selection;
        var shift = (e.Modifiers & InputModifiers.Shift) != 0;
        if (shift && selection.IsActive && Equals(selection.Scope, hit.Scope))
        {
            selection.ExtendTo(hit.Scope, hit.Pos);
            _dragging = true;
            _dragScope = hit.Scope;
            _lastPoint = point;
            _surface.RequestRedraw();
            e.Consume();
            return;
        }

        // Collapse onto the press. Nothing is drawn until a drag widens it, so a plain click reads
        // as "clear the selection" — and stays unconsumed, so the click also does its normal job.
        selection.Begin(hit.Scope, hit.Pos);
        _dragScope = hit.Scope;
        _armed = true;
        _pressPoint = point;
        _lastPoint = point;
        _surface.RequestRedraw();
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (_dragging)
        {
            // Mid-drag the focused dispatch delivers moves in the bubbling phase, including those
            // outside the view. Consuming them re-asserts pointer capture for the next frame.
            if (e.Phase != EventPhase.Bubbling) return;
            _lastPoint = e.Mouse.Point;
            ExtendTo(_lastPoint);
            e.Consume();
            return;
        }

        if (e.Phase != EventPhase.Capturing) return;
        _lastPoint = e.Mouse.Point;
        if (!_armed) return;

        if (!e.Mouse.IsButtonPressed(MouseButton.Left))
        {
            _armed = false;
            return;
        }

        var travel = e.Mouse.Point - _pressPoint;
        if (travel.LengthSquared() < DragThreshold * DragThreshold) return;

        _armed = false;
        _dragging = true;
        ExtendTo(e.Mouse.Point);
        e.Consume();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        // Hover-scoped, like the diff's F hotkey: keys pass through to whatever the pointer is
        // actually over, even while this controller holds focus.
        if (!_pointerInside && !_dragging) return;

        var command = (e.Modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        switch (e.Key)
        {
            case KeyboardKey.C when command:
                if (Copy()) e.Consume();
                return;
            case KeyboardKey.A when command:
                if (SelectAll()) e.Consume();
                return;
            case KeyboardKey.Escape:
                if (_surface.Selection.Clear())
                {
                    _surface.RequestRedraw();
                    e.Consume();
                }
                return;
        }
    }

    private bool Copy()
    {
        var selection = _surface.Selection;
        if (!selection.HasRange || _clipboard == null) return false;
        if (_surface.RowsOf(selection.Scope) is not { } rows) return false;

        var text = DiffSelectionModel.BuildCopyText(rows, selection.Start, selection.End);
        if (text.Length == 0) return false;
        _clipboard.SetText(text);
        return true;
    }

    // Selects the whole file under the pointer — or the one already selected, so Ctrl+A after a
    // drag that ended in a gutter still widens to the file it was selecting in.
    private bool SelectAll()
    {
        var selection = _surface.Selection;
        var scope = selection.IsActive ? selection.Scope : _surface.ClampToScope(_lastPoint, null)?.Scope;
        if (_surface.RowsOf(scope) is not { } rows) return false;
        if (DiffSelectionModel.WholeSpan(rows) is not { } span) return false;

        if (selection.SetRange(scope, span.Start, span.End)) _surface.RequestRedraw();
        return true;
    }

    private void ExtendTo(PointF point)
    {
        if (_surface.ClampToScope(point, _dragScope) is not { } hit) return;
        if (_surface.Selection.ExtendTo(hit.Scope, hit.Pos)) _surface.RequestRedraw();
    }

    private int CountClick(PointF point)
    {
        var now = Environment.TickCount;
        var near = Math.Abs(point.X - _lastClickPoint.X) <= MultiClickSlopPx
            && Math.Abs(point.Y - _lastClickPoint.Y) <= MultiClickSlopPx;
        _clickCount = _hasLastClick && near && unchecked(now - _lastClickTickMs) <= MultiClickThresholdMs
            ? _clickCount + 1
            : 1;
        _lastClickTickMs = now;
        _lastClickPoint = point;
        _hasLastClick = true;
        return _clickCount;
    }
}
