using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The bordered, scrollable card a dialog puts its file/branch list in. A two-axis
/// <see cref="ScrollPane"/> backs it, so an over-long row (a deep path) scrolls horizontally into
/// view in full rather than truncating, and a long list scrolls vertically; both scrollbars
/// auto-hide when their axis fits. <see cref="ScrollPane.FillParent"/> makes the card claim no
/// height of its own, so a fixed-height dialog's body Grow hands it all the leftover space and the
/// frame's outer bar never engages (see <see cref="DialogScrollRegion"/>); place it in a Grow.
/// </summary>
internal sealed record DialogScrollList : Widget
{
    /// <summary>The list content — a column of rows (or an empty-state placeholder).</summary>
    public required View Content { get; init; }

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();

        // The padding rides inside the scroll viewport (wrapping the content), not around it, so the
        // rows keep a gap from the scrollbars and the bars sit flush to the card border. Padding
        // outside the pane would leave the text running up against the scrollbar with no breathing room.
        var pane = new ScrollPane { FillParent = true };
        pane.Children.Add(new PaddingView
        {
            Padding = PaddingStyle.All(Spacing.Sm),
            Children = { Content },
        });
        pane.UseController(input, () => new WheelController(pane));

        var vBar = ScrollBars.CreateVertical(ctx);
        var hBar = ScrollBars.CreateHorizontal(ctx);

        var card = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            {
                new BorderLayoutView
                {
                    Center = pane,
                    East = vBar,
                    South = hBar,
                },
            },
        };
        card.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.InsetBackground);
        card.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));
        card.Use(() => new ScrollSyncController(pane, vBar, hBar));

        return card;
    }

    // Innermost scroll target: scrolls both axes off the wheel and consumes only when it actually
    // moved, so a wheel over a list with nothing to scroll bubbles back out rather than dead-ending.
    private sealed class WheelController(ScrollPane pane) : KeyboardMouseController
    {
        public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
        {
            var moved = false;
            if (e.DeltaY != 0f) moved |= pane.ScrollVertical(-e.DeltaY * Scrolling.WheelStep);
            if (e.DeltaX != 0f) moved |= pane.ScrollHorizontal(-e.DeltaX * Scrolling.WheelStep);
            if (moved) e.Consume();
        }
    }
}
