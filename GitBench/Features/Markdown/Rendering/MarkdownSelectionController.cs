using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.KeyboardModule;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Drag-to-select and Ctrl/Cmd+C over one rendered markdown surface.
///
/// Presses are watched in the capture phase but deliberately left unconsumed: the click still has
/// to reach whatever sits under it, so a link click opens its url. A selection only begins once the
/// pointer travels past <see cref="DragThreshold"/> with the button down, at which point the
/// controller takes focus and consumes moves so the drag keeps tracking outside the surface — the
/// same bargain <see cref="DragRecognizer"/> strikes, and the reason a drag that starts on link
/// text quotes the link instead of following it.
/// <para>
/// The keyboard is scoped to a live selection rather than to hover: the highlight is on screen, a
/// press anywhere outside the surface clears it, and until then Ctrl+C means that highlight
/// wherever the pointer has drifted — including off the end of the reply the drag ran past. With
/// nothing selected every key falls through, so the composer keeps its own copy.
/// </para>
/// </summary>
internal sealed class MarkdownSelectionController : KeyboardMouseController, IProvidesCursor, IDisposable
{
    /// <summary>Travel that turns a press into a drag rather than a click. <see cref="LinkController"/>
    /// arbitrates the same gesture against the same number, so exactly one of the two claims it.</summary>
    internal const float DragThreshold = 3f;

    // Per-frame auto-scroll while the pointer is dragged past an edge, ramped by how far past.
    private const float AutoScrollMaxPerFrame = 24f;

    private readonly MarkdownSelectionScope _scope;
    private readonly View _surface;
    private readonly Context _ctx;
    private readonly InputSystem _input;
    private readonly IFrameTicker? _ticker;
    private readonly IClipboard? _clipboard;
    private readonly ILocalizationService? _localization;

    // A press landed on text; a selection starts if the pointer travels before release.
    private bool _armed;
    private bool _dragging;
    private PointF _pressPoint;
    private PointF _lastPoint;

    public MarkdownSelectionController(
        MarkdownSelectionScope scope,
        View surface,
        Context ctx,
        InputSystem input,
        IFrameTicker? ticker,
        IClipboard? clipboard,
        ILocalizationService? localization)
    {
        _scope = scope;
        _surface = surface;
        _ctx = ctx;
        _input = input;
        _ticker = ticker;
        _clipboard = clipboard;
        _localization = localization;
    }

    // Read only while this controller captures the pointer, i.e. mid-drag; hovering a leaf shows
    // the link cursor or the default from the leaf's own controller.
    public MouseCursor Cursor => MouseCursor.Text;

    public void Dispose() => EndDrag();

    public override void OnFocusLost()
    {
        _armed = false;
        EndDrag();
        if (_scope.Selection.Clear()) _scope.Invalidate();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        // A right-click over a selection asks about it. Anywhere else — a right-click with nothing
        // selected, or one outside the surface — is somebody else's, so it falls through unconsumed.
        if (e.Button == MouseButton.Right)
        {
            if (e.Phase != EventPhase.Capturing || e.State != InputState.Pressed) return;
            if (!_scope.Selection.HasRange) return;
            if (!_surface.Position.ContainsPoint(e.Mouse.Point)) return;
            if (ShowSelectionMenu(e.Mouse.Point)) e.Consume();
            return;
        }

        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Released)
        {
            _armed = false;
            if (!_dragging) return;
            EndDrag();
            e.Consume();
            return;
        }

        // While focused this controller sees every press first, wherever it lands. One landing
        // outside the surface belongs to another one: drop focus, which clears the selection.
        if (e.Phase == EventPhase.Bubbling)
        {
            if (!_surface.Position.ContainsPoint(e.Mouse.Point)) _input.Blur(this);
            return;
        }

        if (e.Phase == EventPhase.Capturing) OnPress(ref e);
    }

    private void OnPress(ref MouseButtonEvent e)
    {
        var point = e.Mouse.Point;
        if (_scope.HitTest(point) is not { } pos)
        {
            if (_scope.Selection.Clear()) _scope.Invalidate();
            return;
        }

        // This surface is now the focused one, so Ctrl+C reaches us rather than whatever list or
        // field held focus.
        _input.StealFocus(this);

        // Collapse onto the press. Nothing is drawn until a drag widens it, so a plain click reads
        // as "clear the selection" — and stays unconsumed, so the click also does its normal job.
        _scope.Selection.Begin(pos);
        _armed = true;
        _pressPoint = point;
        _lastPoint = point;
        _scope.Invalidate();
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
        BeginDrag();
        ExtendTo(e.Mouse.Point);
        e.Consume();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (e.State != InputState.Pressed) return;
        if (!_scope.Selection.HasRange) return;

        var command = (e.Modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        switch (e.Key)
        {
            case KeyboardKey.C when command:
                if (Copy()) e.Consume();
                return;
            case KeyboardKey.Escape:
                if (_scope.Selection.Clear())
                {
                    _scope.Invalidate();
                    e.Consume();
                }
                return;
        }
    }

    private bool Copy()
    {
        var selection = _scope.Selection;
        if (!selection.HasRange || _clipboard == null) return false;

        var text = MarkdownSelectionModel.BuildCopyText(
            _scope.DocumentOrder(), selection.Start, selection.End);
        if (text.Length == 0) return false;
        _clipboard.SetText(text);
        return true;
    }

    private bool ShowSelectionMenu(PointF point)
    {
        if (_clipboard == null || _localization == null) return false;
        var items = new[]
        {
            new RepoBarContextMenu.Item(
                _localization.Strings.Value.CommonCopy, () => Copy(), LucideIcons.Copy),
        };
        return RepoBarContextMenu.Show(_ctx, point, items) != null;
    }

    private void ExtendTo(PointF point)
    {
        if (_scope.Clamp(point) is not { } pos) return;
        if (_scope.Selection.ExtendTo(pos)) _scope.Invalidate();
    }

    private void BeginDrag()
    {
        if (_dragging) return;
        _dragging = true;
        _ticker?.Add(AutoScroll);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _ticker?.Remove(AutoScroll);
    }

    /// <summary>
    /// Scrolls the enclosing pane while a drag hangs past one of its edges and extends the
    /// selection to keep up — moves alone can't do it, since a pointer held still outside the
    /// viewport emits none.
    /// </summary>
    private void AutoScroll(float _)
    {
        if (!_dragging || ScrollHost() is not { } pane) return;
        var viewport = pane.Position;
        if (viewport.Height <= 0) return;

        // GUI coordinates are y-up: above the viewport is a larger y.
        float dy;
        if (_lastPoint.Y > viewport.Top) dy = -Math.Min(AutoScrollMaxPerFrame, _lastPoint.Y - viewport.Top);
        else if (_lastPoint.Y < viewport.Bottom) dy = Math.Min(AutoScrollMaxPerFrame, viewport.Bottom - _lastPoint.Y);
        else return;

        if (!pane.Scroll(dy)) return;
        ExtendTo(_lastPoint);
    }

    private VerticalScrollPane? ScrollHost()
    {
        for (var parent = _surface.Parent; parent != null; parent = parent.Parent)
            if (parent is VerticalScrollPane pane) return pane;
        return null;
    }
}
