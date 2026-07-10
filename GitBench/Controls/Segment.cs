using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>One half of the mode-switcher pill; activates its segment on press. Live hover/press state
/// lives on a <see cref="ButtonState"/> exposed as the widget's <see cref="IInteractable"/> surface, so
/// the parent attaches the controller (<c>segment.WithController&lt;KbmController&gt;()</c>).</summary>
internal sealed record Segment : Widget<ButtonState>
{
    private const float SegmentHeight = 28f;

    public required Prop<string?> Label { get; init; }
    public required BorderRadiusStyle Radius { get; init; }
    public required ISegmentModel Model { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(new Command(Model.Activate));

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Height = SegmentHeight,
        BorderRadius = Radius,
        Background = Theme.Color(s => s.ModeSwitcher.SegmentBackground(Model.IsActive.Value, state.Hovered.Value)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                Children =
                [
                    new Text
                    {
                        Value = Label,
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.ModeSwitcher.SegmentText(Model.IsActive.Value, state.Hovered.Value)),
                    },
                ],
            },
        ],
    };
}
