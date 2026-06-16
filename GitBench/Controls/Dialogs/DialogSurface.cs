using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The app's modal-dialog layer: a <see cref="DialogSurfaceView"/> with its
/// <see cref="DialogPresenter"/> wired onto it, so showing and closing dialogs is driven
/// entirely off the message bus. Mount once, near the top of the z-order.
/// </summary>
internal sealed record DialogSurface : Widget
{
    protected override View CreateView(Context ctx)
    {
        var surface = new DialogSurfaceView(ctx.Require<InputSystem>());
        surface.Behaviors.Add(new DialogPresenter(ctx, surface));
        return surface;
    }
}
