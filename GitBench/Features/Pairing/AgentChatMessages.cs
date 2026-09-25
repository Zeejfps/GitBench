using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>A rounded bubble on one side of the chat, as wide as what it holds up to the width
/// left beside the gutter on the other side.</summary>
internal sealed record AgentChatBubble : Widget
{
    private const int Gutter = 48;

    public required IWidget Content { get; init; }
    public required bool Trailing { get; init; }
    public required Func<ThemeStyles, uint> Fill { get; init; }

    /// <summary>Takes the whole width beside the gutter rather than fitting what it holds, for content
    /// such as a code block or a rule that has no width of its own to fit.</summary>
    public bool Fills { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var fill = Fill;
        IWidget bubble = new Box
        {
            Background = Theme.Color(fill),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            BorderRadius = BorderRadiusStyle.All(Radius.Lg * 2),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = Spacing.Xl, Right = Spacing.Xl, Top = Spacing.Lg, Bottom = Spacing.Lg,
                    },
                    Children = [Content],
                },
            ],
        };

        return new Padding
        {
            Amount = Trailing ? new PaddingStyle { Left = Gutter } : new PaddingStyle { Right = Gutter },
            Children =
            [
                new Row
                {
                    MainAxis = Trailing ? MainAxisAlignment.End : MainAxisAlignment.Start,
                    Children = [Fills ? new Grow { Child = bubble } : new Shrink { Child = bubble }],
                },
            ],
        };
    }
}

/// <summary>What the user said, on the right in a bubble sized to the text, with any code they sent
/// along under it.</summary>
internal sealed record AgentChatUserBubble : Widget
{
    public required IReadable<string> Text { get; init; }
    public IWidget? Attachment { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var text = Text;
        IWidget body = new TranscriptBodyText
        {
            Value = Prop.Bind(() => text.Value),
            Color = Theme.Color(s => s.Palette.TextPrimary),
            SizesToText = true,
        };
        if (Attachment is { } attachment)
            body = new Column
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [body, attachment],
            };

        return new AgentChatBubble { Content = body, Trailing = true, Fill = static s => s.Palette.SurfaceSelectedSubtle };
    }
}

/// <summary>What the agent said, on the left in a bubble of its markdown, with a copy under it while
/// it is hovered.</summary>
internal sealed record AgentChatReply : Widget
{
    public required IReadable<string> Text { get; init; }

    /// <summary>The agent is still writing it: its words are revealed as they come rather than shown
    /// in the lumps they arrive in.</summary>
    public bool Streams { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var text = Text;
        var reveal = new StreamReveal(text, ctx.Require<IFrameTicker>(), Streams);
        var hovered = new State<bool>(false);
        return new KbmInput
        {
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
            Child = new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new AgentChatBubble
                    {
                        Content = new TranscriptMarkdownBody { Text = reveal.Shown },
                        Trailing = false,
                        Fills = true,
                        Fill = static s => s.Palette.SurfaceRaised,
                    },
                    new Row
                    {
                        Opacity = hovered.Bind(h => h ? 1f : 0f),
                        Children = [new CopyIconButton { Label = static s => s.CommonCopy, GetText = () => text.Value }],
                    },
                ],
            },
        }.Use(_ => reveal);
    }
}

/// <summary>The agent at work on its next words: three dots breathing in turn in a bubble of the
/// agent's.</summary>
/// <remarks>Mount it fresh behind a <see cref="Show"/>: the pulse stops on unmount.</remarks>
internal sealed record AgentChatTypingBubble : Widget
{
    private const float DotSize = 7f;
    private const float Lift = 3f;
    private const float Stagger = 0.18f;

    protected override IWidget Build(Context ctx)
    {
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        var dots = new IWidget[3];
        for (var i = 0; i < dots.Length; i++)
        {
            var offset = i * Stagger;
            float Breath()
            {
                var phase = pulse.Phase.Value - offset;
                return 0.5f - 0.5f * MathF.Cos((phase - MathF.Floor(phase)) * MathF.Tau);
            }

            dots[i] = new Box
            {
                Width = DotSize,
                Height = DotSize,
                BorderRadius = BorderRadiusStyle.All(DotSize / 2f),
                Background = Theme.Color(s => s.Palette.TextSecondary),
                Opacity = Prop.Bind(() => 0.35f + 0.65f * Breath()),
                TranslationY = Prop.Bind(() => Lift * Breath()),
            };
        }

        return new AgentChatBubble
        {
            Content = new Padding
            {
                Amount = new PaddingStyle { Top = Spacing.Xs, Bottom = Spacing.Xs },
                Children = [new Row { Gap = Spacing.Xs, CrossAxis = CrossAxisAlignment.Center, Children = dots }],
            },
            Trailing = false,
            Fill = static s => s.Palette.SurfaceRaised,
        }.Use(_ => pulse);
    }
}
