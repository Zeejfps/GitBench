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

    /// <summary>Lets its owner move the region to either end — to follow a conversation, or to go
    /// back to what sits at the top of it.</summary>
    public ScrollRegionHandle? Handle { get; init; }

    protected override View CreateView(Context ctx)
    {
        var pane = new VerticalScrollPane { StretchContent = StretchContent, FillParent = FillParent };
        var handle = Handle;
        if (handle is not null) pane.Use(() => handle.Attach(pane));
        pane.Children.Add(new FlexItem { Grow = 1, Child = Content.BuildView(ctx) });
        pane.UseController(ctx.Require<InputSystem>(), () => handle is null
            ? WheelScrollController.For(pane)
            : new WheelScrollController((_, dy) => handle.UserScroll(dy)));

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

/// <summary>
/// Moves a <see cref="ScrollRegion"/> to its top, or pins it to its bottom the way a chat keeps up
/// with its newest message: pinned, every layout pass snaps it back to the end, so rows that are
/// still being laid out or still growing stay in view. Scrolling with the wheel releases the pin,
/// and scrolling back down to the end takes it again. Does nothing while the region is unmounted.
/// </summary>
internal sealed class ScrollRegionHandle
{
    private VerticalScrollPane? _pane;
    private bool _pinned = true;

    public void ScrollToTop()
    {
        _pinned = false;
        _pane?.ScrollToTop();
    }

    /// <summary>Pins the region to its end, from now until the reader scrolls away.</summary>
    public void FollowBottom()
    {
        _pinned = true;
        _pane?.ScrollToBottom();
    }

    internal bool UserScroll(float dy)
    {
        if (_pane is not { } pane) return false;
        var moved = pane.Scroll(dy);
        if (moved) _pinned = dy > 0 && AtBottom(pane);
        return moved;
    }

    internal IDisposable Attach(VerticalScrollPane pane)
    {
        _pane = pane;
        void OnLaidOut(float _)
        {
            // Scroll answers false once already at the end, so this settles rather than relaying
            // out forever.
            if (_pinned) pane.ScrollToBottom();
        }

        pane.ScrollPositionChanged += OnLaidOut;
        return new ActionDisposable(() =>
        {
            pane.ScrollPositionChanged -= OnLaidOut;
            if (ReferenceEquals(_pane, pane)) _pane = null;
        });
    }

    private static bool AtBottom(VerticalScrollPane pane) => pane.Scale >= 1f || pane.ScrollNormalized >= 0.999f;
}
