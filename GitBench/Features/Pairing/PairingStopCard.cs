using GitBench.Controls;
using GitBench.Features.Markdown.Rendering;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>The stop card's place in the session's pane, under the goal and roadmap: the open stop,
/// a placeholder while the agent works out the next one, and nothing once the session is over.</summary>
internal sealed record PairingStopSlot : Widget
{
    private const int Over = -1;
    private const int Between = 0;

    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var shown = Over;
        // Keyed on the stop's number: a change within the stop doesn't rebuild the card.
        return new Switch<int>
        {
            Value = new Derived<int>(() => store.Stop.Value?.Stop.Number ?? (store.IsLive ? Between : Over)),
            Case = number =>
            {
                var fromPlaceholder = shown == Between;
                shown = number;
                if (number == Over || (number != Between && store.Stop.Value is null)) return Empty.Widget;
                if (number == Between) return new FadeIn { Bloom = true, Child = new PairingStopPlaceholder() };
                IWidget card = new PairingStopCard { Store = store, Stop = store.Stop.Value! };
                return fromPlaceholder ? new FadeIn { Child = card } : card;
            },
        };
    }
}

/// <summary>The stop card's shape, breathing, while the agent works out the next stop.</summary>
internal sealed record PairingStopPlaceholder : Widget
{
    public const string PlaceholderId = "pairing-stop-placeholder";

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();
        var pulse = new Pulse(ctx.Require<IFrameTicker>());
        pulse.Start();

        Prop<uint> Fill(float dim) => Prop.Bind(() =>
            SkeletonPainter.Fill(theme.Styles.Value.Palette.TextPrimary, pulse.Value.Value, dim));
        IWidget Bar(float width, float height, float dim = 1f) => new Box
        {
            Width = width,
            Height = height,
            Background = Fill(dim),
            BorderRadius = BorderRadiusStyle.All(height / 2f),
        };

        var view = new Box
        {
            Id = PlaceholderId,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Sm,
                            Children =
                            [
                                Bar(48f, 8f, dim: 0.8f),
                                Bar(200f, 14f),
                                Bar(150f, 8f, dim: 0.7f),
                                Bar(220f, 8f, dim: 0.8f),
                                Bar(180f, 8f, dim: 0.8f),
                                Bar(120f, 8f, dim: 0.8f),
                            ],
                        },
                    ],
                },
            ],
        }.BuildView(ctx);
        view.Use(() => pulse);
        return view;
    }
}

/// <summary>
/// The stop the user is on, in the session's pane: its number and Show change, the title, its file and line — a
/// click goes back there, the full path is in the tooltip — and what there, then why, then where the agent's code is.
/// </summary>
internal sealed record PairingStopCard : Widget
{
    public const string CardId = "pairing-stop";
    public const string LocationId = "pairing-stop-location";
    public const string ShowChangeId = "pairing-show-change";

    public required PairingStore Store { get; init; }
    public required OpenStop Stop { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var open = Stop;
        var stop = open.Stop;
        var loc = ctx.Localization();

        return new Box
        {
            Id = CardId,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new Grow
                                        {
                                            Child = new Text
                                            {
                                                Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingStopNumber(stop.Number)),
                                                FontSize = FontSize.Caption,
                                                Weight = FontWeight.Bold,
                                                Color = Theme.Color(s => s.Palette.Accent),
                                            },
                                        },
                                        new ButtonWidget
                                        {
                                            Id = ShowChangeId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                            Command = new Command(store.RevealStop),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingShowChange) }],
                                        }
                                        .WithTooltip(L.T(s => s.PairingGoToStop))
                                        .WithController<KbmController>(),
                                    ],
                                },
                                new Text
                                {
                                    Value = stop.Title,
                                    FontSize = FontSize.Heading,
                                    Weight = FontWeight.Bold,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                                new Column
                                {
                                    Gap = Spacing.Xs,
                                    CrossAxis = CrossAxisAlignment.Start,
                                    Children =
                                    [
                                        new ButtonWidget
                                        {
                                            Id = LocationId,
                                            Style = ButtonStyle.Bare(state => Theme.Color(s => state.Hovered.Value ? s.Palette.Accent : 0x00000000u)),
                                            Command = new Command(store.RevealStop),
                                            ContentInset = PaddingStyle.All(Spacing.None),
                                            Children =
                                            [
                                                new Column
                                                {
                                                    CrossAxis = CrossAxisAlignment.Stretch,
                                                    Children =
                                                    [
                                                        new Text
                                                        {
                                                            Value = FileAndLine(open),
                                                            FontSize = FontSize.Caption,
                                                            FontFamily = MonoFonts.Regular,
                                                            Color = Theme.Color(s => s.Palette.Accent),
                                                        },
                                                        new Box { Height = 1f, Background = Foreground.Color },
                                                    ],
                                                },
                                            ],
                                        }
                                        .WithTooltip(Prop.Bind<string?>(() => $"{loc.Strings.Value.PairingGoToStop}\n{stop.Target.Path}"))
                                        .WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = Prop.Bind<string?>(() => Detail(loc.Strings.Value, open)),
                                            FontSize = FontSize.Caption,
                                            Wrap = TextWrap.Wrap,
                                            Color = Theme.Color(s => s.Palette.TextMuted),
                                        },
                                    ],
                                },
                                new MarkdownText { Text = new State<string>(stop.Reason) },
                                new PairingDraftNote { Store = store, Draft = open.Draft },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static string FileAndLine(OpenStop open)
    {
        var name = PathText.Split(open.Stop.Target.Path).Name;
        return open.Location is StopLocation.NewFile ? name : $"{name}:{DraftLine(open.Draft)}";
    }

    private static string Detail(Strings s, OpenStop open) => open.Location switch
    {
        StopLocation.OnSymbol => open.Stop.Target.Symbol,
        StopLocation.Insertion insertion => s.PairingStopAfter(insertion.After),
        StopLocation.NewFile => s.PairingStopNewFile,
        _ => throw new ArgumentOutOfRangeException(nameof(open), open.Location, "Unknown location."),
    };

    private static int DraftLine(StopDraft draft) => draft.Place switch
    {
        DraftPlace.Replace replace => replace.Lines.From,
        DraftPlace.InsertAfter insert => insert.Line.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(draft), draft.Place, "Unknown draft place."),
    };
}

/// <summary>
/// The stop's controls, pinned under what the panel scrolls for the whole session: Accept &amp;
/// next — which puts the agent's code into the file and moves on — Next, which moves on with the
/// file as it is, and Skip — and apart from them End, which asks first. Once the code is in, Next is
/// the one to press. Between stops the stop's controls stay where they are, unavailable. Anything to tell the agent goes in the conversation below.
/// </summary>
internal sealed record PairingStopActions : Widget
{
    public const string BarId = "pairing-stop-actions";
    public const string AcceptAndNextId = "pairing-accept-next";
    public const string NextId = "pairing-next";
    public const string SkipId = "pairing-skip";
    public const string EndId = "pairing-end";

    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var loc = ctx.Localization();
        var keys = ctx.KeyMap();
        var bus = ctx.Require<IMessageBus>();

        var idle = new Derived<bool>(() => store.Stop.Value is not null && store.Activity.Value == StopActivity.Idle);
        var accepted = new Derived<bool>(() => store.Stop.Value?.DraftState is DraftState.Taken);

        void AcceptAndNext() => _ = store.AcceptAndNextAsync();
        void Next() => _ = store.DoneAsync();
        void ConfirmEnd() => bus.Broadcast(new ShowDialogMessage(onClose => new ConfirmAgentChatDialog
        {
            Title = loc.Strings.Value.PairingEndTitle,
            Body = loc.Strings.Value.PairingEndBody,
            ActionLabel = loc.Strings.Value.PairingEnd,
            CancelLabel = loc.Strings.Value.PairingKeepPairing,
            OnClose = onClose,
            OnConfirm = store.EndByUser,
        }));

        Prop<string?> Tooltip(Func<Strings, string> text, KeyCommand command) =>
            Prop.Bind<string?>(() => $"{text(loc.Strings.Value)} ({keys.Display(command)})");

        IWidget NextButton(bool primary) => new ButtonWidget
        {
            Id = NextId,
            Style = primary ? ButtonStyle.Filled(static s => s.Palette.Accent) : ButtonStyle.Outline(static s => s.Palette.TextBody),
            Command = new Command(Next, idle),
            Children = [new ButtonLabel { Value = L.T(s => s.PairingNext) }],
        }
        .WithTooltip(Tooltip(s => s.PairingNextTooltip, KeyCommand.PairingNext))
        .WithController<KbmController>();

        IWidget bar = new Box
        {
            Id = BarId,
            BorderSize = new BorderSizeStyle { Top = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Md),
                    Children =
                    [
                        new Wrap
                        {
                            Gap = Spacing.Sm,
                            RunGap = Spacing.Sm,
                            Children =
                            [
                                new Switch<bool>
                                {
                                    Value = accepted,
                                    Case = isIn => isIn
                                        ? NextButton(primary: true)
                                        : new Row
                                        {
                                            Gap = Spacing.Sm,
                                            CrossAxis = CrossAxisAlignment.Center,
                                            Children =
                                            [
                                                new ButtonWidget
                                                {
                                                    Id = AcceptAndNextId,
                                                    Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                                    Command = new Command(AcceptAndNext, idle),
                                                    Children = [new ButtonLabel { Value = L.T(s => s.PairingAcceptAndNext) }],
                                                }
                                                .WithTooltip(Tooltip(s => s.PairingAcceptAndNextTooltip, KeyCommand.PairingAcceptAndNext))
                                                .WithController<KbmController>(),
                                                NextButton(primary: false),
                                            ],
                                        },
                                },
                                new ButtonWidget
                                {
                                    Id = SkipId,
                                    Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                    Command = new Command(store.Skip, idle),
                                    Children = [new ButtonLabel { Value = L.T(s => s.PairingSkip) }],
                                }.WithController<KbmController>(),
                                new Grow { Child = Empty.Widget },
                                new ButtonWidget
                                {
                                    Id = EndId,
                                    Style = ButtonStyle.Outline(static s => s.Status.DangerText),
                                    Command = new Command(ConfirmEnd),
                                    Children = [new ButtonLabel { Value = L.T(s => s.PairingEnd) }],
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        };

        return bar.Use(_ =>
        {
            var gates = new SubscriptionGroup();
            gates.Add(idle);
            gates.Add(accepted);
            return gates;
        });
    }
}

/// <summary>Where the user talks to the agent — questions, and what they did instead of what it
/// suggested — in the shape of the walkthrough's composer. Enter sends, Shift+Enter breaks the line.</summary>
internal sealed record PairingAskField : Widget
{
    public const string InputId = "pairing-ask";
    public const string SendId = "pairing-ask-send";

    public required AgentConversation Conversation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var conversation = Conversation;
        var loc = ctx.Localization();

        var field = new GrowingDescriptionField(ctx, 0f, 120f) { Id = InputId };
        field.Bind(loc.Strings, s => field.PlaceholderText = s.PairingAskPlaceholder);
        var hasText = new Derived<bool>(() => !string.IsNullOrWhiteSpace(field.TextValue.Value));

        void Send()
        {
            if (!hasText.Value) return;
            conversation.Say(field.Text.ToString());
            field.Clear();
        }

        field.OnSubmit = Send;
        field.OnEscape = field.EndEditing;
        field.Use(() =>
        {
            var subscriptions = new SubscriptionGroup();
            subscriptions.Add(field.EndEditing);
            subscriptions.Add(hasText);
            return subscriptions;
        });

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
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.End,
                            Children =
                            [
                                new Grow { Child = new Raw { View = field } },
                                new ButtonWidget
                                {
                                    Id = SendId,
                                    Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                    ContentInset = ButtonStyle.Filled(static s => s.Palette.Accent).IconOnlyInset,
                                    Command = new Command(Send, hasText),
                                    Children = [new ButtonIcon { Value = LucideIcons.Push }],
                                }
                                .WithTooltip(L.T(s => s.PairingAskSend))
                                .WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
