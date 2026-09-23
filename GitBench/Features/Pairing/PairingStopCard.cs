using GitBench.Controls;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>The stop card's place at the top of what the panel scrolls: the open stop, or nothing
/// between stops.</summary>
internal sealed record PairingStopSlot : Widget
{
    public required PairingStore Store { get; init; }

    /// <summary>Which part of the stop this slot shows.</summary>
    public required bool Actions { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var actions = Actions;
        // Keyed on the stop's number: the stop's test moving along must not rebuild the card and
        // lose the note being typed.
        return new Switch<int>
        {
            Value = new Derived<int>(() => store.Stop.Value?.Stop.Number ?? 0),
            Case = number => number == 0 || store.Stop.Value is not { } stop
                ? Empty.Widget
                : actions
                    ? new PairingStopActions { Store = store, Stop = stop }
                    : new PairingStopCard { Store = store, Stop = stop },
        };
    }
}

/// <summary>
/// The stop the user is on, at the top of what the panel scrolls: its number and kind, the title,
/// where it is — a click goes back there — and why, then its test and where the agent's code is.
/// </summary>
internal sealed record PairingStopCard : Widget
{
    public const string CardId = "pairing-stop";
    public const string LocationId = "pairing-stop-location";

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
                                        new Text
                                        {
                                            Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingStopNumber(stop.Number)),
                                            FontSize = FontSize.Caption,
                                            Weight = FontWeight.Bold,
                                            Color = Theme.Color(s => s.Palette.Accent),
                                        },
                                        new Text
                                        {
                                            Value = stop.Kind == PairingStopKind.Test ? L.T(s => s.PairingKindTest) : L.T(s => s.PairingKindEdit),
                                            FontSize = FontSize.Caption,
                                            Color = Theme.Color(s => s.Palette.TextSecondary),
                                        },
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
                                new ButtonWidget
                                {
                                    Id = LocationId,
                                    Style = ButtonStyle.Bare(_ => Theme.Color(s => s.Palette.TextBody)),
                                    Command = new Command(store.RevealStop),
                                    ContentInset = PaddingStyle.All(Spacing.None),
                                    Children =
                                    [
                                        new Text
                                        {
                                            Value = Prop.Bind<string?>(() => Where(loc.Strings.Value, open)),
                                            FontSize = FontSize.Caption,
                                            FontFamily = MonoFonts.Regular,
                                            Overflow = TextOverflow.Ellipsis,
                                            Color = Theme.Color(s => s.Palette.Accent),
                                        },
                                    ],
                                }
                                .WithTooltip(L.T(s => s.PairingGoToStop))
                                .WithController<KbmController>(),
                                new MarkdownText { Text = new State<string>(stop.Reason) },
                                stop.Kind == PairingStopKind.Test
                                    ? new Switch<StopTest?>
                                    {
                                        Value = new Derived<StopTest?>(() => store.Stop.Value?.Test),
                                        Case = test => new PairingTestSection { Store = store, Test = test },
                                    }
                                    : Empty.Widget,
                                new PairingDraftNote { Draft = open.Draft },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static string Where(Strings s, OpenStop open) => open.Location switch
    {
        StopLocation.OnSymbol => $"{open.Stop.Target.Path}:{DraftLine(open.Draft)} · {open.Stop.Target.Symbol}",
        StopLocation.Insertion insertion =>
            $"{open.Stop.Target.Path}:{DraftLine(open.Draft)} · {s.PairingStopAfter(insertion.After)}",
        StopLocation.NewFile => s.PairingStopNewFile(open.Stop.Target.Path),
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
/// The open stop's controls, pinned under what the panel scrolls: what they are busy with, Accept —
/// which puts the agent's code into the file and finishes the stop — Done, which finishes it with
/// what the user typed, and Skip. Anything to tell the agent goes in the conversation below.
/// </summary>
internal sealed record PairingStopActions : Widget
{
    public const string AcceptId = "pairing-accept";
    public const string DoneId = "pairing-done";
    public const string SkipId = "pairing-skip";
    public const string ShowChangeId = "pairing-show-change";

    public required PairingStore Store { get; init; }
    public required OpenStop Stop { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var loc = ctx.Localization();

        var idle = new Derived<bool>(() => store.Activity.Value == StopActivity.Idle);
        var canFinish = new Derived<bool>(() => idle.Value && store.Stop.Value is { } open && PairingStore.CanFinish(open));

        void Accept() => _ = store.AcceptAsync();
        void Done() => _ = store.DoneAsync();

        IWidget bar = new Box
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
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => store.Activity.Value switch
                                    {
                                        StopActivity.Checking => loc.Strings.Value.PairingChecking,
                                        StopActivity.RunningTest => loc.Strings.Value.PairingRunningTest,
                                        StopActivity.Accepting => loc.Strings.Value.PairingAccepting,
                                        StopActivity.Idle => null,
                                        _ => throw new InvalidOperationException("Unknown activity."),
                                    }),
                                    Visible = Prop.Bind(() => store.Activity.Value != StopActivity.Idle),
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                                new Row
                                {
                                    Gap = Spacing.Sm,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new ButtonWidget
                                        {
                                            Id = AcceptId,
                                            Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                            Command = new Command(Accept, canFinish),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingAccept) }],
                                        }
                                        .WithTooltip(L.T(s => s.PairingAcceptTooltip))
                                        .WithController<KbmController>(),
                                        new ButtonWidget
                                        {
                                            Id = DoneId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                            Command = new Command(Done, canFinish),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingDone) }],
                                        }
                                        .WithTooltip(L.T(s => s.PairingDoneTooltip))
                                        .WithController<KbmController>(),
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
                                            Id = ShowChangeId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                            Command = new Command(store.RevealStop),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingShowChange) }],
                                        }
                                        .WithTooltip(L.T(s => s.PairingGoToStop))
                                        .WithController<KbmController>(),
                                    ],
                                },
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
            gates.Add(canFinish);
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

    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var loc = ctx.Localization();

        var field = new GrowingDescriptionField(ctx, 0f, 120f) { Id = InputId };
        field.Bind(loc.Strings, s => field.PlaceholderText = s.PairingAskPlaceholder);
        var hasText = new Derived<bool>(() => !string.IsNullOrWhiteSpace(field.TextValue.Value));

        void Send()
        {
            if (!hasText.Value) return;
            store.Say(field.Text.ToString());
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
