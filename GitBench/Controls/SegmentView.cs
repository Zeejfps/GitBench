using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>One half of the mode-switcher pill; activates its segment on click.</summary>
internal sealed record SegmentView : Widget
{
    private const float SegmentHeight = 28f;

    public required string Label { get; init; }
    public required BorderRadiusStyle Radius { get; init; }
    public required SegmentViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;
        var hovered = new State<bool>(false);

        return new KbmInput
        {
            OnClick = Model.Activate,
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
            Child = new Box
            {
                Height = SegmentHeight,
                BorderRadius = Radius,
                Background = Prop.Bind(() =>
                    Model.IsActive.Value ? styles.Value.ModeSwitcher.SegmentActiveBackground :
                    hovered.Value ? styles.Value.ModeSwitcher.SegmentHoverBackground :
                    styles.Value.ModeSwitcher.SegmentIdleBackground),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Left = 12, Right = 12 },
                        Children =
                        [
                            new Text
                            {
                                Value = Label,
                                HAlign = TextAlignment.Center,
                                VAlign = TextAlignment.Center,
                                Color = Prop.Bind(() =>
                                    Model.IsActive.Value ? styles.Value.ModeSwitcher.SegmentActiveText :
                                    hovered.Value ? styles.Value.ModeSwitcher.SegmentHoverText :
                                    styles.Value.ModeSwitcher.SegmentIdleText),
                            },
                        ],
                    },
                ],
            },
        };
    }
}
