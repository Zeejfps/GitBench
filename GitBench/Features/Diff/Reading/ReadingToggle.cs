using GitBench.Controls;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff.Reading;

/// <summary>
/// The reading-mode control: a two-segment switch between the full diff and the abridged one, the
/// retention manifest beside it, and a live "abridging" state while a plan is being made.
/// </summary>
/// <remarks>
/// Two labelled segments rather than one button, because the reader has to be able to tell at a
/// glance which diff is in front of them. An abridged diff is a diff with things missing from it —
/// mistaking it for the whole change is the one failure this feature must not have, so the state is
/// spelled out rather than implied by a tint.
///
/// Abridging takes the better part of a minute. That state says so, breathes so the window does not
/// look hung, names each file as the model opens it, and stays pressable to cancel.
/// </remarks>
internal sealed record ReadingToggle : Widget
{
    private const float SegmentHeight = 22f;
    private const float DotSize = 6f;

    public required ReadingModeCoordinator Reading { get; init; }

    /// <summary>Show the abridged diff, running an abridgement if there is no plan yet.</summary>
    public required Action OnAbridge { get; init; }

    /// <summary>Put the full diff back.</summary>
    public required Action OnFull { get; init; }

    /// <summary>Abandon a run in flight.</summary>
    public required Action OnCancel { get; init; }

    protected override IWidget Build(Context ctx) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Switch<ReadingPhase>
            {
                Value = Reading.Phase,
                Case = phase => phase switch
                {
                    ReadingPhase.Working => Working(),
                    ReadingPhase.Failed => Failed(),
                    _ => Segments(abridged: phase == ReadingPhase.Showing),
                },
            },
        ],
    };

    // The resting control. Whichever diff is on screen is the filled segment; the other is the way
    // back to it. Both are always pressable, and once a plan exists neither costs a round trip.
    private IWidget Segments(bool abridged) => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            Manifest(),
            new Box
            {
                BorderRadius = BorderRadiusStyle.All(Radius.Md),
                BorderSize = BorderSizeStyle.All(1),
                BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
                Children =
                [
                    new Row
                    {
                        Children =
                        [
                            Segment(L.T(s => s.ReadingFull), active: !abridged, onPress: OnFull),
                            Segment(L.T(s => s.ReadingAbridged), active: abridged, onPress: OnAbridge),
                        ],
                    },
                ],
            },
        ],
    };

    // Mid-run: a breathing dot, what the model is doing this second, and a press that stops it.
    private IWidget Working() => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = Prop.Bind(() => Reading.Activity.Value ?? string.Empty),
                Visible = Prop.Bind(() => !string.IsNullOrEmpty(Reading.Activity.Value)),
                FontSize = FontSize.Caption,
                Color = Theme.Color(s => s.Palette.TextMuted),
                Overflow = TextOverflow.Ellipsis,
                VAlign = TextAlignment.Center,
            },
            new Box
            {
                Height = SegmentHeight,
                BorderRadius = BorderRadiusStyle.All(Radius.Md),
                BorderSize = BorderSizeStyle.All(1),
                BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm },
                        Children =
                        [
                            new Row
                            {
                                Gap = Spacing.Xs,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children =
                                [
                                    new WorkingDot(),
                                    new Text
                                    {
                                        Value = L.T(s => s.ReadingWorking),
                                        FontSize = FontSize.Caption,
                                        Color = Theme.Color(s => s.Palette.TextPrimary),
                                        VAlign = TextAlignment.Center,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            }.WithController<KbmController>(new ButtonState(new Command(OnCancel))),
        ],
    };

    // A failed run keeps the reason on screen next to a way to ask again.
    private IWidget Failed() => new Row
    {
        Gap = Spacing.Sm,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = Prop.Bind(() => Reading.Status.Value ?? string.Empty),
                FontSize = FontSize.Caption,
                Color = Theme.Color(s => s.Status.Danger),
                Overflow = TextOverflow.Ellipsis,
                VAlign = TextAlignment.Center,
            },
            new ButtonWidget
            {
                Style = ButtonStyle.Outline(s => s.Palette.TextSecondary),
                Command = new Command(OnAbridge),
                Children = [new ButtonLabel { Value = L.T(s => s.ReadingRetry) }],
            }.WithController<KbmController>(),
        ],
    };

    // How much of the change is hidden, counted from the diff itself rather than reported by the
    // model — a reader deciding whether to trust an abridgement needs a number nobody invented.
    private IWidget Manifest() => new Text
    {
        Value = Prop.Bind(() => Reading.Status.Value ?? string.Empty),
        Visible = Prop.Bind(() => !string.IsNullOrEmpty(Reading.Status.Value)),
        FontSize = FontSize.Caption,
        Color = Theme.Color(s => s.Palette.TextSecondary),
        Overflow = TextOverflow.Ellipsis,
        VAlign = TextAlignment.Center,
    };

    private static IWidget Segment(Prop<string> label, bool active, Action onPress)
    {
        var state = new ButtonState(new Command(onPress));
        var box = new Box
        {
            Height = SegmentHeight,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = active
                ? Theme.Color(s => s.Palette.SurfaceHoverStrong)
                : Prop.Deferred<uint>(ctx =>
                {
                    var hover = Theme.Color(s => s.Palette.SurfaceHover).ToReadable(ctx);
                    return Prop.Bind(() => state.Hovered.Value ? hover.Value : 0u);
                }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm },
                    Children =
                    [
                        new Text
                        {
                            Value = label,
                            FontSize = FontSize.Caption,
                            Color = active
                                ? Theme.Color(s => s.Palette.TextPrimary)
                                : Prop.Deferred<uint>(ctx =>
                                {
                                    var primary = Theme.Color(s => s.Palette.TextPrimary).ToReadable(ctx);
                                    var secondary = Theme.Color(s => s.Palette.TextSecondary).ToReadable(ctx);
                                    return Prop.Bind(() => state.Hovered.Value ? primary.Value : secondary.Value);
                                }),
                            VAlign = TextAlignment.Center,
                        },
                    ],
                },
            ],
        };
        return box.WithController<KbmController>(state);
    }

    /// <summary>A breathing dot: the window is not hung, it is waiting on a model.</summary>
    private sealed record WorkingDot : Widget
    {
        protected override IWidget Build(Context ctx)
        {
            var theme = ctx.Theme();
            var pulse = new Pulse(ctx.Require<IFrameTicker>());
            pulse.Start();

            return new Box
            {
                Width = DotSize,
                Height = DotSize,
                BorderRadius = BorderRadiusStyle.All(DotSize / 2f),
                Background = Prop.Bind(() =>
                    Fade(theme.Styles.Value.Palette.TextPrimary, 0.3f + 0.7f * pulse.Value.Value)),
            };
        }

        private static uint Fade(uint color, float alpha)
        {
            var a = (uint)Math.Clamp((int)(alpha * 255f), 0, 255);
            return (color & 0x00FFFFFFu) | (a << 24);
        }
    }
}
