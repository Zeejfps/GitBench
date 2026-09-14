using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Assistant;
using GitBench.Features.Markdown.Rendering;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// The review window's third column while a walkthrough is up: who is narrating and where the
/// reviewer is, the current step's title, prose and highlighted lines, the exchange about it read as
/// the assistant chat is, then Back / Next and a composer to ask the narrator a question — or,
/// before the first step, the narrator at work. Bound to the window's
/// <see cref="ReviewWalkthroughStore"/>.
/// </summary>
internal sealed record ReviewWalkthroughRail : Widget
{
    public const string RailId = "walkthrough-rail";

    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Id = RailId,
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new WalkthroughRailHeader { Model = Model },
                    new Grow
                    {
                        Child = new DialogScrollRegion
                        {
                            FillParent = true,
                            Content = new Padding
                            {
                                Amount = PaddingStyle.All(Spacing.Lg),
                                Children = [new WalkthroughCardBody { Model = Model }],
                            },
                        },
                    },
                    new WalkthroughRailFooter { Model = Model },
                ],
            },
        ],
    };
}

/// <summary>The rail's top band: the step counter on the leading edge, the narrator on the trailing
/// one — or, once the narrator has gone away, that fact and a Clear button.</summary>
internal sealed record WalkthroughRailHeader : Widget
{
    public const string ClearId = "walkthrough-clear";

    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var model = Model;

        return new Box
        {
            Height = ReviewHeaderBar.BarHeight,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind<string?>(() => model.Card.Value switch
                                        {
                                            WalkthroughCard.Step step =>
                                                loc.Strings.Value.WalkthroughStepCounter(step.Index + 1, step.Count),
                                            WalkthroughCard.Finished => loc.Strings.Value.WalkthroughFinished,
                                            _ => string.Empty,
                                        }),
                                        FontSize = FontSize.Body,
                                        Weight = FontWeight.Bold,
                                        Color = Theme.Color(s => s.Palette.TextPrimary),
                                        VAlign = TextAlignment.Center,
                                    },
                                },
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => (model.Phase.Value, model.Card.Value) switch
                                    {
                                        (WalkthroughPhase.Narrating { Waiting: true } n, _) => loc.Strings.Value.WalkthroughNarratedBy(n.Who.Label),
                                        (WalkthroughPhase.Narrating n, _) => loc.Strings.Value.WalkthroughNarratorBusy(n.Who.Label),
                                        (WalkthroughPhase.Disconnected, WalkthroughCard.Preparing) => loc.Strings.Value.WalkthroughNotStarted,
                                        (WalkthroughPhase.Disconnected, _) => loc.Strings.Value.WalkthroughDisconnected,
                                        _ => string.Empty,
                                    }),
                                    FontSize = FontSize.Caption,
                                    Color = Prop.Bind(() => model.Phase.Value is WalkthroughPhase.Disconnected
                                        ? ctx.Theme().Styles.Value.Status.DangerText
                                        : ctx.Theme().Styles.Value.Palette.TextSecondary),
                                    VAlign = TextAlignment.Center,
                                    Overflow = TextOverflow.Ellipsis,
                                },
                                new Show
                                {
                                    When = new Derived<bool>(() =>
                                        model.Phase.Value is WalkthroughPhase.Disconnected
                                        || model.Card.Value is WalkthroughCard.Finished),
                                    Then = () => new ButtonWidget
                                    {
                                        Id = ClearId,
                                        Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                        Command = new Command(model.Clear),
                                        Children = [new ButtonLabel { Value = L.T(s => s.WalkthroughClear) }],
                                    }.WithController<KbmController>(),
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>What the rail's scrolling middle shows for the current card: the wait for the first
/// step, a step, or the closing summary. Rebuilt per card, so each step's markdown starts fresh.</summary>
internal sealed record WalkthroughCardBody : Widget
{
    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        return new Switch<WalkthroughCard>
        {
            Value = model.Card,
            Case = card => card switch
            {
                WalkthroughCard.Preparing => new WalkthroughPreparingCard { Model = model },
                WalkthroughCard.Step step => new WalkthroughStepCard { Model = model, Step = step },
                WalkthroughCard.Finished finished => new WalkthroughFinishedCard { Summary = finished.SummaryMarkdown },
                WalkthroughCard.None => Empty.Widget,
                _ => throw new ArgumentOutOfRangeException(nameof(card), card, "Unknown card."),
            },
        };
    }
}

/// <summary>The wait for a narrator's first step: the exchange so far — what it says meanwhile, or
/// died with — and, while it is still at work, the narrator "reading the change" over a breathing
/// skeleton. Once it has given up, what it said is what is left to read.</summary>
internal sealed record WalkthroughPreparingCard : Widget
{
    public const string CardId = "walkthrough-preparing";

    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var loc = ctx.Localization();

        return new Box
        {
            Id = CardId,
            Children =
            [
                new WalkthroughExchange
                {
                    Model = model,
                    ThinkingLabel = Prop.Bind<string?>(() => model.Phase.Value is WalkthroughPhase.Narrating n
                        ? loc.Strings.Value.WalkthroughPreparing(n.Who.Label)
                        : string.Empty),
                },
            ],
        };
    }
}

/// <summary>One step: its title, its body, the numbered highlights, any line the diff could not
/// land, and — under a hairline, once there is any — the exchange about it.</summary>
internal sealed record WalkthroughStepCard : Widget
{
    public required ReviewWalkthroughStore Model { get; init; }
    public required WalkthroughCard.Step Step { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var content = Step.Content;
        var children = new List<IWidget>
        {
            new Text
            {
                Value = content.Title,
                FontSize = FontSize.Heading,
                Weight = FontWeight.Bold,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.Palette.TextPrimary),
            },
            new MarkdownText { Text = new State<string>(content.BodyMarkdown) },
        };

        if (content.Spotlights.Count > 0)
            children.Add(new WalkthroughSpotlightList { Spotlights = content.Spotlights, OnFocus = model.FocusSpotlight });

        children.Add(new WalkthroughNotice { Failure = model.LastFailure });
        children.Add(new Show
        {
            When = new Derived<bool>(() => model.Exchange.Value.Count > 0 || model.IsComposing.Value),
            Then = () => new Box
            {
                BorderSize = new BorderSizeStyle { Top = 1 },
                BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.BorderSubtle }),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Top = Spacing.Lg },
                        Children = [new WalkthroughExchange { Model = model }],
                    },
                ],
            },
        });

        return new Column
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. children],
        };
    }
}

/// <summary>The closing card after <c>walkthrough_end</c> with a summary: the summary, and nothing
/// to step through.</summary>
internal sealed record WalkthroughFinishedCard : Widget
{
    public required string Summary { get; init; }

    protected override IWidget Build(Context ctx) => new MarkdownText { Text = new State<string>(Summary) };
}

/// <summary>The current step's highlighted ranges as a numbered list, each numbered as its pin in
/// the diff is; a click brings that range back into view.</summary>
internal sealed record WalkthroughSpotlightList : Widget
{
    private const float NumberColumnWidth = 20f;

    public static string RowId(int number) => $"walkthrough-spotlight-{number}";

    public required IReadOnlyList<ReviewSpotlight> Spotlights { get; init; }
    public required Action<int> OnFocus { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var rows = new List<IWidget>(Spotlights.Count + 1)
        {
            new Text
            {
                Value = L.T(s => s.WalkthroughHighlights),
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Color = Theme.Color(s => s.Palette.TextSecondary),
            },
        };

        for (var i = 0; i < Spotlights.Count; i++)
            rows.Add(new WalkthroughSpotlightRow { Number = i + 1, Spotlight = Spotlights[i], OnFocus = OnFocus });

        return new Column
        {
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children = [.. rows],
        };
    }

    /// <summary>One highlight: its pin number, where it is, and the narrator's note on it.</summary>
    private sealed record WalkthroughSpotlightRow : Widget
    {
        public required int Number { get; init; }
        public required ReviewSpotlight Spotlight { get; init; }
        public required Action<int> OnFocus { get; init; }

        protected override IWidget Build(Context ctx)
        {
            var number = Number;
            var onFocus = OnFocus;
            var spotlight = Spotlight;
            var location = spotlight.From == spotlight.To
                ? $"{spotlight.Path}:{spotlight.From.Value}"
                : $"{spotlight.Path}:{spotlight.From.Value}-{spotlight.To.Value}";

            var detail = new List<IWidget>
            {
                new Text
                {
                    Value = location,
                    FontSize = FontSize.Caption,
                    FontFamily = MonoFonts.Regular,
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                    Overflow = TextOverflow.Ellipsis,
                },
            };
            if (spotlight.Note is { Length: > 0 } note)
                detail.Add(new Text
                {
                    Value = note,
                    FontSize = FontSize.Body,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Palette.TextBody),
                });

            return new ButtonWidget
            {
                Id = RowId(number),
                Style = ButtonStyle.Bare(_ => Theme.Color(s => s.Palette.TextBody)),
                Command = new Command(() => onFocus(number - 1)),
                ContentInset = PaddingStyle.All(Spacing.None),
                Children =
                [
                    new Box
                    {
                        Width = NumberColumnWidth,
                        Children =
                        [
                            new Text
                            {
                                Value = number.ToString(),
                                FontSize = FontSize.Caption,
                                Weight = FontWeight.Bold,
                                Color = Theme.Color(s => s.Palette.Accent),
                            },
                        ],
                    },
                    new Grow
                    {
                        Child = new Column
                        {
                            Gap = Spacing.Hair,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = [.. detail],
                        },
                    },
                ],
            }.WithController<KbmController>();
        }
    }
}

/// <summary>A line the diff could not land, in the narrator's own coordinates so the reviewer can
/// tell what was meant. Collapses when the last apply resolved everything.</summary>
internal sealed record WalkthroughNotice : Widget
{
    public required IReadable<ReviewLineResolution?> Failure { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var failure = Failure;
        return new Text
        {
            Value = Prop.Bind<string?>(() => failure.Value is { } f ? Describe(loc.Strings.Value, f) : null),
            Visible = Prop.Bind(() => failure.Value != null),
            FontSize = FontSize.Caption,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(s => s.Status.Warning),
        };
    }

    private static string Describe(Strings s, ReviewLineResolution failure) => failure switch
    {
        ReviewLineResolution.NoSuchFile f => s.WalkthroughNoticeNoSuchFile(f.Path),
        ReviewLineResolution.NotInDiff f => s.WalkthroughNoticeNotInDiff(f.Line.Path, f.Line.Line.Value),
        ReviewLineResolution.Unavailable f => s.WalkthroughNoticeUnavailable(f.Path, f.Reason),
        ReviewLineResolution.Resolved => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown resolution."),
    };
}

/// <summary>The conversation under the current card, read as the assistant chat is: the reviewer's
/// questions, the narrator's prose and the turns that went wrong, each in the transcript's own row,
/// then the narrator thinking while its next words are on the way. Empty until there is any of it.</summary>
/// <remarks>Keyed on the exchange, so moving to another step swaps the whole list rather than
/// reconciling one step's messages into another's, and each message fades in once, as it lands.</remarks>
internal sealed record WalkthroughExchange : Widget
{
    public required ReviewWalkthroughStore Model { get; init; }

    /// <summary>What the thinking placeholder says the narrator is doing; the chat's "Thinking…" unless told otherwise.</summary>
    public Prop<string?> ThinkingLabel { get; init; } = L.T(s => s.AssistantThinking);

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var thinkingLabel = ThinkingLabel;
        return new Column
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Switch<ObservableList<WalkthroughMessage>>
                {
                    Value = model.Exchange,
                    Case = exchange => new Each<WalkthroughMessage>
                    {
                        Items = exchange,
                        Template = new WalkthroughMessageRow(),
                        Gap = Spacing.Lg,
                        CrossAxis = CrossAxisAlignment.Stretch,
                    },
                },
                new Show
                {
                    When = model.IsComposing,
                    Then = () => new AssistantThinkingIndicator { Label = thinkingLabel },
                },
            ],
        };
    }
}

/// <summary>One message of the exchange, in the transcript row for its kind: the reviewer's
/// question as a "You" turn with where its selection was, the narrator's prose as a reply, a
/// failure or refusal as the notice it is.</summary>
internal sealed record WalkthroughMessageRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var message = ctx.Require<WalkthroughMessage>();
        var loc = ctx.Localization();

        IWidget content = message switch
        {
            WalkthroughMessage.Question question => new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new TranscriptMessageRow
                    {
                        Text = new State<string>(question.Text),
                        Label = L.T(s => s.AssistantYou),
                        LabelColor = static s => s.Palette.TextSecondary,
                    },
                    new Text
                    {
                        Value = Prop.Bind<string?>(() => question.Selection is { } quote
                            ? loc.Strings.Value.WalkthroughWithSelection(quote.Location)
                            : null),
                        Visible = question.Selection != null,
                        FontSize = FontSize.Caption,
                        Overflow = TextOverflow.Ellipsis,
                        Color = Theme.Color(s => s.Palette.TextMuted),
                    },
                ],
            },
            WalkthroughMessage.Narration narration => new TranscriptReplyRow { Text = narration.Text },
            WalkthroughMessage.Failure failure => new TranscriptNoticeRow
            {
                Text = new State<string>(failure.Text),
                Tone = TranscriptNoticeTone.Error,
            },
            WalkthroughMessage.Refusal refusal => new TranscriptNoticeRow
            {
                Text = new State<string>(refusal.Text),
                Tone = TranscriptNoticeTone.Refusal,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, "Unknown message."),
        };

        return new FadeIn { Child = content };
    }
}

/// <summary>The rail's bottom band: Back and Next over the Ask composer.</summary>
internal sealed record WalkthroughRailFooter : Widget
{
    public const string BackId = "walkthrough-back";
    public const string NextId = "walkthrough-next";

    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var keys = ctx.KeyMap();

        return new Box
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Show
                                {
                                    When = new Derived<bool>(() => model.Card.Value is WalkthroughCard.Step),
                                    Then = () => new Row
                                    {
                                        Gap = Spacing.Sm,
                                        CrossAxis = CrossAxisAlignment.Center,
                                        Children =
                                        [
                                            new ButtonWidget
                                            {
                                                Id = BackId,
                                                Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                                Command = new Command(model.Back, model.CanGoBack),
                                                Children = [new ButtonLabel { Value = L.T(s => s.WalkthroughBack) }],
                                            }
                                            .WithTooltip(keys.Display(KeyCommand.WalkthroughBack))
                                            .WithController<KbmController>(),
                                            new Grow
                                            {
                                                Child = new ButtonWidget
                                                {
                                                    Id = NextId,
                                                    Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                                    Command = new Command(model.Next, model.CanGoNext),
                                                    Children = [new ButtonLabel { Value = L.T(s => s.WalkthroughNext) }],
                                                }
                                                .WithTooltip(keys.Display(KeyCommand.WalkthroughNext))
                                                .WithController<KbmController>(),
                                            },
                                        ],
                                    },
                                },
                                new Show
                                {
                                    When = new Derived<bool>(() =>
                                        model.Card.Value is WalkthroughCard.Step && model.Phase.Value is WalkthroughPhase.Narrating),
                                    Then = () => new WalkthroughAskField { Model = model },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
