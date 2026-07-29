using GitBench.Platform;
using ZGF.Desktop;
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

    public LinkController(RichTextView view, IPlatformShell shell)
    {
        _view = view;
        _shell = shell;
    }

    /// <summary>Hand over a link segment, default arrow elsewhere.</summary>
    public MouseCursor Cursor => throw new NotImplementedException();

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        throw new NotImplementedException();
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        throw new NotImplementedException();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        throw new NotImplementedException();
    }
}
