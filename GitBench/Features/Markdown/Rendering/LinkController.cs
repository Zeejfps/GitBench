using GitBench.Platform;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Pointer behavior for links inside a <see cref="RichTextView"/>: hover shows the hand cursor
/// (<see cref="IProvidesCursor"/>, read by the input system while this controller is the hovered
/// target) and recolors the link via <see cref="RichTextView.SetHoveredLink"/>; a left click on a
/// link segment opens its url through <see cref="IPlatformShell.OpenUrl"/> and consumes the
/// press. Everything routes through <see cref="RichTextView.LinkAt"/>, so hit-testing lives in
/// one place. Attached by <see cref="RichText"/> per mounted lifetime, the diff pane's
/// <c>DiffMouseController</c> pattern.
/// </summary>
internal sealed class LinkController : KeyboardMouseController, IProvidesCursor
{
    private readonly RichTextView _view;
    private readonly IPlatformShell _shell;
    private string? _hoveredUrl;

    public LinkController(RichTextView view, IPlatformShell shell)
    {
        _view = view;
        _shell = shell;
    }

    /// <summary>Hand over a link segment, default arrow elsewhere.</summary>
    public MouseCursor Cursor => _hoveredUrl != null ? MouseCursor.Hand : MouseCursor.Default;

    // Enter matters as much as move: hover is established by the input system's RefreshHover,
    // which fires enter with the real pointer position but delivers no move to a freshly hovered
    // controller — without this the cursor and recolor lag one event behind.
    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        UpdateHover(e.Mouse.Point);
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        UpdateHover(e.Mouse.Point);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _hoveredUrl = null;
        _view.SetHoveredLink(null);
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        // Open on the press alone so a click (press + release) opens exactly once.
        if (e.Button != MouseButton.Left || e.State != InputState.Pressed) return;

        if (_view.LinkAt(e.Mouse.Point) is not { } url) return;
        _shell.OpenUrl(url);
        e.Consume();
    }

    private void UpdateHover(PointF point)
    {
        _hoveredUrl = _view.LinkAt(point);
        _view.SetHoveredLink(_hoveredUrl);
    }
}
