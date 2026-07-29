using GitBench.Controls;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// The panel as a floating window would be: placed where it was left, sized to what it was resized
/// to, and ringed by the band that resizes it.
/// </summary>
internal sealed record AssistantPanelFrame : Widget
{
    private const float EnterRise = 8f;

    protected override View CreateView(Context ctx)
    {
        var placement = ctx.Require<AssistantPanelPlacement>();

        var content = new FadeIn
        {
            Rise = EnterRise,
            Child = new Stack
            {
                Children =
                [
                    new AssistantResizeBand(),
                    new Padding
                    {
                        Amount = PaddingStyle.All((int)AssistantPanelPlacement.GripSize),
                        Children = [new AssistantOverlayCard()],
                    },
                ],
            },
        };

        return new AssistantPlacementView(placement, content.BuildView(ctx));
    }
}

/// <summary>
/// Fills the overlay layer, tells the placement how much room there is, and puts the panel where the
/// placement says — mirrored, so a stored spot is an inset from the leading edge either way.
/// </summary>
internal sealed class AssistantPlacementView : ContainerView
{
    private readonly AssistantPanelPlacement _placement;

    public AssistantPlacementView(AssistantPanelPlacement placement, View child)
    {
        _placement = placement;
        Children.Add(child);
        // Watched only so a drag marks this view dirty; layout reads the live value, which the same
        // pass may have just re-clamped.
        this.Bind(placement.Rect, _ => SetDirty());
    }

    protected override void OnLayoutChildren()
    {
        var position = Position;
        _placement.SetHost(position.Width, position.Height);

        var rect = _placement.Rect.Value;
        var grip = AssistantPanelPlacement.GripSize;
        var width = rect.Width + 2f * grip;
        var height = rect.Height + 2f * grip;
        var left = IsRtl
            ? position.Right - rect.X - rect.Width - grip
            : position.Left + rect.X - grip;
        var top = position.Top - rect.Y + grip;

        foreach (var child in Children)
        {
            child.LeftConstraint = left;
            child.BottomConstraint = top - height;
            child.WidthConstraint = width;
            child.HeightConstraint = height;
            child.LayoutSelf();
        }
    }
}

/// <summary>The band of resize zones around the panel: a corner at each end of the top and bottom
/// strips, and a side zone down each edge between them.</summary>
internal sealed record AssistantResizeBand : Widget
{
    protected override IWidget Build(Context ctx) => new Column
    {
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new AssistantResizeStrip
            {
                Height = AssistantPanelPlacement.GripSize,
                Leading = AssistantGrip.TopLeading,
                Middle = AssistantGrip.Top,
                Trailing = AssistantGrip.TopTrailing,
            },
            new Grow
            {
                Child = new AssistantResizeStrip
                {
                    Leading = AssistantGrip.Leading,
                    Trailing = AssistantGrip.Trailing,
                },
            },
            new AssistantResizeStrip
            {
                Height = AssistantPanelPlacement.GripSize,
                Leading = AssistantGrip.BottomLeading,
                Middle = AssistantGrip.Bottom,
                Trailing = AssistantGrip.BottomTrailing,
            },
        ],
    };
}

/// <summary>One horizontal run of the band: a fixed zone at each end and, on the top and bottom
/// strips, a stretching one between them. The middle of the side strip is the panel itself.</summary>
internal sealed record AssistantResizeStrip : Widget
{
    public required AssistantGrip Leading { get; init; }
    public required AssistantGrip Trailing { get; init; }
    public AssistantGrip? Middle { get; init; }

    protected override IWidget Build(Context ctx) => new Row
    {
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new AssistantResizeGrip { Grip = Leading },
            new Grow
            {
                Child = Middle is { } middle
                    ? new AssistantResizeGrip { Grip = middle, Stretches = true }
                    : Empty.Widget,
            },
            new AssistantResizeGrip { Grip = Trailing },
        ],
    };
}

/// <summary>One resize zone: invisible, but it owns the pointer over it and says so with the cursor.</summary>
internal sealed record AssistantResizeGrip : Widget
{
    public required AssistantGrip Grip { get; init; }

    /// <summary>Set for the zone that runs the length of a strip; the corners keep their fixed width.</summary>
    public bool Stretches { get; init; }

    public static string IdFor(AssistantGrip grip) => grip switch
    {
        AssistantGrip.Leading => "assistant-grip-leading",
        AssistantGrip.Trailing => "assistant-grip-trailing",
        AssistantGrip.Top => "assistant-grip-top",
        AssistantGrip.Bottom => "assistant-grip-bottom",
        AssistantGrip.TopLeading => "assistant-grip-top-leading",
        AssistantGrip.TopTrailing => "assistant-grip-top-trailing",
        AssistantGrip.BottomLeading => "assistant-grip-bottom-leading",
        _ => "assistant-grip-bottom-trailing",
    };

    protected override IWidget Build(Context ctx)
    {
        var placement = ctx.Require<AssistantPanelPlacement>();
        var input = ctx.Require<InputSystem>();
        var grip = Grip;

        var box = new Box { Id = IdFor(grip) };
        if (!Stretches) box = box with { Width = AssistantPanelPlacement.GripSize };

        return box.WithController(
            input, view => new AssistantPanelResizeController(placement, input, view, grip));
    }
}
