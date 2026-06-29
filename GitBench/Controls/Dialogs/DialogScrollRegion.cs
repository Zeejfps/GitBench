using GitBench.Features.Operations;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Wraps dialog body content in a vertical scroll viewport whose scrollbar appears only when the
/// content can't fit the height it's given. When <see cref="CenterView"/> caps a dialog to the
/// window, the frame hands this region less height than the content wants and it scrolls instead
/// of overflowing; when the body fits, the viewport is the content's natural height, the bar stays
/// hidden, and the dialog looks exactly as it did before. This is the single place every dialog
/// gets "never taller than the window" without each one wiring its own scroll machinery.
/// </summary>
internal sealed record DialogScrollRegion : Widget
{
    public required IWidget Content { get; init; }

    /// <summary>
    /// When true this region claims no height of its own and instead fills the space its parent
    /// allots it, scrolling its content internally past that — so a list can absorb a fixed-height
    /// dialog's leftover space (the dialog, not the list, stays window-sized) while still scrolling
    /// when there are too many rows. The caller must place it in a <c>Grow</c>. When false (the
    /// default, used by the frame around the whole body) the region reports its content height so
    /// the dialog sizes to content and only scrolls once the window caps it.
    /// </summary>
    public bool FillParent { get; init; }

    protected override View CreateView(Context ctx)
    {
        var pane = new VerticalScrollPane { FillParent = FillParent, StretchContent = true };
        // Wrap the content in a Grow so the pane's stretch reaches it: when the viewport is taller
        // than the content, the content is laid out at the viewport height and its own Grow children
        // (e.g. a fill-parent list) get real slack to expand into.
        pane.Children.Add(new FlexItem { Grow = 1, Child = Content.BuildView(ctx) });
        pane.UseController(ctx.Require<InputSystem>(), () => new VerticalScrollPaneWheelController(pane));

        var bar = ScrollBars.CreateVertical(ctx);
        // FlexRowView skips invisible children, so the bar costs no horizontal gutter until the
        // content actually overflows. Scale < 1 means the viewport is smaller than the content —
        // i.e. there is somewhere to scroll to.
        bar.IsVisible = false;
        pane.ScrollPositionChanged += _ => bar.IsVisible = pane.Scale < 1f;

        var container = new ContainerView();
        container.Children.Add(new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Child = pane },
                bar,
            },
        });
        container.Use(() => new VerticalScrollBarSyncController(pane, bar));
        return container;
    }
}
