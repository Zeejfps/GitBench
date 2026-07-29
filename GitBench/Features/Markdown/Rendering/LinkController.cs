using GitBench.Platform;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Pointer behavior for links inside a <see cref="RichTextView"/>: hover shows the hand cursor
/// (<see cref="IProvidesCursor"/>, read by the input system while this controller is the hovered
/// target) and recolors the link via <see cref="RichTextView.SetHoveredLink"/>; a left click on an
/// http(s) link segment opens its url through <see cref="IPlatformShell.OpenUrl"/> and consumes
/// the release. Any other url is inert — no cursor, no hover, no open. Everything routes through
/// <see cref="OpenableLinkAt"/>, so hit-testing lives in one place. Attached by
/// <see cref="RichText"/> per mounted lifetime, the diff pane's <c>DiffMouseController</c>
/// pattern.
/// </summary>
internal sealed class LinkController : KeyboardMouseController, IProvidesCursor
{
    private readonly RichTextView _view;
    private readonly IPlatformShell _shell;

    private string? _pressedUrl;
    private PointF _pressPoint;

    public LinkController(RichTextView view, IPlatformShell shell)
    {
        _view = view;
        _shell = shell;
    }

    /// <summary>Hand over a link segment, default arrow elsewhere. The view's hover state is the
    /// single source of truth — this controller only writes it via <see cref="UpdateHover"/>.</summary>
    public MouseCursor Cursor => _view.HoveredLinkUrl != null ? MouseCursor.Hand : MouseCursor.Default;

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
        if (_pressedUrl != null && Travelled(e.Mouse.Point)) _pressedUrl = null;
        UpdateHover(e.Mouse.Point);
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _pressedUrl = null;
        _view.SetHoveredLink(null);
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Capturing) return;
        if (e.Button != MouseButton.Left) return;

        if (e.State == InputState.Pressed)
        {
            // The press is only armed, never acted on: link text has to stay draggable, and it is
            // the travel before the release that decides whether the reader was following the link
            // or quoting it.
            _pressedUrl = OpenableLinkAt(e.Mouse.Point);
            _pressPoint = e.Mouse.Point;
            return;
        }

        var url = _pressedUrl;
        _pressedUrl = null;
        if (url == null || Travelled(e.Mouse.Point)) return;
        if (OpenableLinkAt(e.Mouse.Point) != url) return;

        Open(url);
        e.Consume();
    }

    // The pointer has left the press behind, so the gesture is a drag and belongs to the selection
    // controller — which recognizes it against the same threshold.
    private bool Travelled(PointF point)
    {
        var threshold = MarkdownSelectionController.DragThreshold;
        return (point - _pressPoint).LengthSquared() >= threshold * threshold;
    }

    // IPlatformShell.OpenUrl is contracted not to throw, but the shell is injected and this runs
    // inside input dispatch, where anything that escapes takes the window down with it.
    private void Open(string url)
    {
        try
        {
            _shell.OpenUrl(url);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LinkController] Failed to open '{url}': {e.Message}");
        }
    }

    private void UpdateHover(PointF point) => _view.SetHoveredLink(OpenableLinkAt(point));

    // The markdown rendered here is untrusted (assistant output, README content) and the shell
    // launches whatever it is handed — a UNC or local path would run an executable — so only
    // http(s) counts as a link at all; everything else never reaches the shell and is given no
    // clickable affordance either.
    private string? OpenableLinkAt(PointF point)
    {
        if (_view.LinkAt(point) is not { } url) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? url : null;
    }
}
