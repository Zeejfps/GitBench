using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls;

/// <summary>
/// A vertical scroll viewport whose scrollbar appears only when the content can't fit the height
/// it's given, and takes no gutter while it fits — unlike the framework <c>ScrollArea</c>, which
/// reserves one. A dialog body that fits looks exactly as it would unscrolled; capped to the window
/// (see <see cref="Dialogs.DialogFrame"/>) it scrolls instead of overflowing.
/// </summary>
internal sealed record ScrollRegion : Widget
{
    public required IWidget Content { get; init; }

    /// <summary>Claim no height of the region's own, so a parent Grow hands it the leftover space and
    /// the content scrolls inside that (see <see cref="VerticalScrollPane.FillParent"/>).</summary>
    public bool FillParent { get; init; }

    /// <summary>Lay the content out at the viewport height while it is shorter, so its own Grow
    /// children get real slack to expand into (see <see cref="VerticalScrollPane.StretchContent"/>).</summary>
    public bool StretchContent { get; init; }

    protected override View CreateView(Context ctx)
    {
        var pane = new VerticalScrollPane { StretchContent = StretchContent, FillParent = FillParent };
        pane.Children.Add(new FlexItem { Grow = 1, Child = Content.BuildView(ctx) });
        pane.UseController(ctx.Require<InputSystem>(), () => WheelScrollController.For(pane));

        var bar = ScrollBars.CreateVertical(ctx);
        bar.IsVisible = false;

        var container = new ContainerView();
        container.Children.Add(new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                // Shrink as well as Grow: a flex child that can't shrink lays out at its intrinsic
                // width even when that overruns the row, and the pane's intrinsic width is the
                // widest body child's unwrapped width — a wrapping paragraph measures as one long
                // line. Without this the pane sizes past the frame and forces its content to that
                // width, so nothing wraps and the body draws under the dialog's clip.
                new FlexItem { Grow = 1, Shrink = 1, Child = pane },
                bar,
            },
        });
        container.Use(() => new ScrollSyncController(pane, bar));
        return container;
    }
}
