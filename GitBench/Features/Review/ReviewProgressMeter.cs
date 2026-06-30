using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// A thin horizontal progress bar: a rounded track with a left-anchored fill sized to
/// <see cref="Fraction"/> (0..1). Used in the review header (increments reviewed) and action bar
/// (files viewed) to turn the bare "X / Y" captions into a glanceable meter.
/// </summary>
internal sealed record ReviewProgressMeter : Widget
{
    private const float TrackWidth = 84f;
    private const float TrackHeight = 6f;

    public required IReadable<float> Fraction { get; init; }
    public required Prop<uint> Fill { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Width = TrackWidth,
        Height = TrackHeight,
        BorderRadius = BorderRadiusStyle.All(TrackHeight / 2f),
        Background = Theme.Color(s => s.Palette.ScrollBarTrackBg),
        Children =
        [
            new Row
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new Box
                    {
                        Width = Prop.Bind(() => Math.Clamp(Fraction.Value, 0f, 1f) * TrackWidth),
                        BorderRadius = BorderRadiusStyle.All(TrackHeight / 2f),
                        Background = Fill,
                    },
                ],
            },
        ],
    };
}
