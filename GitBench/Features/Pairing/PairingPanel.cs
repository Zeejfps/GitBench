using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>The Pairing panel's place beside the content panel: there while the repository on
/// screen has a session, gone otherwise.</summary>
internal sealed record PairingPanelSlot : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var sessions = ctx.Require<PairingSessions>();
        var preferences = ctx.Require<PreferencesService>();
        return new Switch<PairingSession?>
        {
            Value = sessions.Active,
            Case = session => session is null
                ? Empty.Widget
                : new ResizableSidebar
                {
                    Edge = SidebarEdge.Trailing,
                    InitialWidth = preferences.Current.PairingPanelWidth,
                    MinResizeWidth = 280f,
                    OnWidthChanged = w => preferences.Update(p => p with { PairingPanelWidth = w }),
                    Content = new PairingPanel { Session = session },
                },
        };
    }
}

/// <summary>
/// One pairing session, the way the user follows it: who drives and whose turn it is, the goal and
/// the roadmap with what the agent last changed in it, the stop they are on with Done, and the
/// conversation under it with a field to ask the agent.
/// </summary>
internal sealed record PairingPanel : Widget
{
    public const string PanelId = "pairing-panel";

    public required PairingSession Session { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var session = Session;
        var store = session.Store;
        var scroll = new ScrollRegionHandle();
        IWidget panel = new Box
        {
            Id = PanelId,
            Background = Theme.Color(s => s.Palette.Surface),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new PairingHeader { Session = session },
                        new Grow
                        {
                            Child = new ScrollRegion
                            {
                                FillParent = true,
                                Handle = scroll,
                                Content = new Padding
                                {
                                    Amount = PaddingStyle.All(Spacing.Lg),
                                    Children =
                                    [
                                        new Column
                                        {
                                            Gap = Spacing.Lg,
                                            CrossAxis = CrossAxisAlignment.Stretch,
                                            Children =
                                            [
                                                new PairingStopSlot { Store = store, Actions = false },
                                                new PairingOutcome { Store = store },
                                                new PairingGoalAndRoadmap { Store = store },
                                                new PairingConversation { Store = store },
                                            ],
                                        },
                                    ],
                                },
                            },
                        },
                        new Show
                        {
                            When = new Derived<bool>(() => store.Phase.Value is PairingPhase.Starting or PairingPhase.Running),
                            Then = () => new PairingAskField { Store = store },
                        },
                        new PairingStopSlot { Store = store, Actions = true },
                    ],
                },
            ],
        };

        return panel.Use(_ => Follow(store, scroll, ctx.Require<IUiDispatcher>()));
    }

    // What the user says pins the panel to the end of the conversation, which then keeps up with
    // the reply as it streams in; a new stop takes the panel back to the top, where its card is.
    private static IDisposable Follow(PairingStore store, ScrollRegionHandle scroll, IUiDispatcher dispatcher)
    {
        var subscriptions = new SubscriptionGroup();
        var count = store.Messages.Count;
        subscriptions.Add(store.Messages.Subscribe(_ =>
        {
            var grew = store.Messages.Count > count;
            count = store.Messages.Count;
            if (grew && store.Messages[^1] is PairingMessage.FromUser) scroll.FollowBottom();
        }));

        var stopNumber = store.Stop.Value?.Stop.Number ?? 0;
        subscriptions.Add(store.Stop.Subscribe(stop =>
        {
            var number = stop?.Stop.Number ?? 0;
            if (number == stopNumber) return;
            stopNumber = number;
            // Posted, so the card is in the tree before the region goes to it.
            if (number != 0) dispatcher.Post(scroll.ScrollToTop);
        }));
        return subscriptions;
    }
}

/// <summary>The panel's top band: the title, whose turn it is, and End — or Close once it's over.</summary>
internal sealed record PairingHeader : Widget
{
    public const string EndId = "pairing-end";
    public const string CloseId = "pairing-close";

    public required PairingSession Session { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var session = Session;
        var store = session.Store;
        var sessions = ctx.Require<PairingSessions>();
        var live = new Derived<bool>(() => store.Phase.Value is PairingPhase.Starting or PairingPhase.Running);

        return new Box
        {
            Height = Sizes.RowHeight + Spacing.Md * 2,
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
                                new Text
                                {
                                    Value = L.T(s => s.PairingTitle),
                                    FontSize = FontSize.Body,
                                    Weight = FontWeight.Bold,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                    VAlign = TextAlignment.Center,
                                },
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind<string?>(() => Status(loc.Strings.Value, store)),
                                        FontSize = FontSize.Caption,
                                        Color = Prop.Bind(() => store.Phase.Value is PairingPhase.Failed or PairingPhase.Disconnected
                                            ? ctx.Theme().Styles.Value.Status.DangerText
                                            : ctx.Theme().Styles.Value.Palette.TextSecondary),
                                        VAlign = TextAlignment.Center,
                                        Overflow = TextOverflow.Ellipsis,
                                    },
                                },
                                new Switch<bool>
                                {
                                    Value = live,
                                    Case = isLive => isLive
                                        ? new ButtonWidget
                                        {
                                            Id = EndId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                            Command = new Command(store.EndByUser),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingEnd) }],
                                        }.WithController<KbmController>()
                                        : new ButtonWidget
                                        {
                                            Id = CloseId,
                                            Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                            Command = new Command(() => sessions.Close(session.Repo.Id)),
                                            Children = [new ButtonLabel { Value = L.T(s => s.PairingClose) }],
                                        }.WithController<KbmController>(),
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static string Status(Strings s, PairingStore store) => store.Phase.Value switch
    {
        PairingPhase.Starting => s.PairingStatusStarting(store.Harness),
        PairingPhase.Running { Waiting: true } => s.PairingStatusYourTurn,
        PairingPhase.Running => s.PairingStatusThinking(store.Harness),
        PairingPhase.Ended => s.PairingStatusEnded,
        PairingPhase.Failed => s.PairingStatusFailed,
        PairingPhase.Disconnected => s.PairingStatusDisconnected,
        _ => throw new InvalidOperationException("Unknown phase."),
    };
}

/// <summary>How the session ended, when it has: the agent's summary, or why it failed.</summary>
internal sealed record PairingOutcome : Widget
{
    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx) => new Switch<PairingPhase>
    {
        Value = Store.Phase,
        Case = phase => phase switch
        {
            PairingPhase.Ended { Summary: { } summary } => new Column
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new PairingSectionHeading { Text = L.T(s => s.PairingSummary) },
                    new MarkdownText { Text = new State<string>(summary) },
                ],
            },
            PairingPhase.Failed failed => new TranscriptNoticeRow { Text = new State<string>(failed.Reason), Tone = TranscriptNoticeTone.Error },
            PairingPhase.Disconnected gone => new TranscriptNoticeRow { Text = new State<string>(gone.Reason), Tone = TranscriptNoticeTone.Advisory },
            _ => Empty.Widget,
        },
    };
}

/// <summary>A small bold caption over a section of the panel.</summary>
internal sealed record PairingSectionHeading : Widget
{
    public required Prop<string?> Text { get; init; }

    protected override IWidget Build(Context ctx) => new Text
    {
        Value = Text,
        FontSize = FontSize.Caption,
        Weight = FontWeight.Bold,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };
}

/// <summary>The goal as the user wrote it, and the agent's roadmap toward it.</summary>
internal sealed record PairingGoalAndRoadmap : Widget
{
    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        return new Column
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new PairingSectionHeading { Text = L.T(s => s.PairingGoal) },
                new Text
                {
                    Value = store.Goal,
                    FontSize = FontSize.Body,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Palette.TextBody),
                },
                new PairingSectionHeading { Text = L.T(s => s.PairingRoadmap) },
                new Switch<IReadOnlyList<RoadmapEntry>>
                {
                    Value = store.Roadmap,
                    Case = entries => entries.Count == 0
                        ? new Text
                        {
                            Value = L.T(s => s.PairingRoadmapEmpty),
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(s => s.Palette.TextMuted),
                        }
                        : new Column
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = [.. entries.Select(entry => (IWidget)new PairingRoadmapRow { Entry = entry })],
                        },
                },
            ],
        };
    }
}

/// <summary>One milestone: ticked when done, tagged when the last revision added it, faded and
/// tagged when the last revision dropped it.</summary>
internal sealed record PairingRoadmapRow : Widget
{
    public required RoadmapEntry Entry { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var entry = Entry;
        var dropped = entry.Change == RoadmapChange.Removed;
        var children = new List<IWidget>
        {
            new Text
            {
                Value = entry.Done ? LucideIcons.CheckSquare : LucideIcons.Square,
                FontFamily = LucideIcons.FontFamily,
                FontSize = FontSize.Caption,
                Width = Sizes.Icon,
                Color = Theme.Color(s => entry.Done ? s.Status.Success : s.Palette.TextMuted),
            },
            new Grow
            {
                Child = new Text
                {
                    Value = entry.Title,
                    FontSize = FontSize.Body,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => dropped || entry.Done ? s.Palette.TextMuted : s.Palette.TextBody),
                },
            },
        };
        if (entry.Change != RoadmapChange.Kept)
            children.Add(new Text
            {
                Value = dropped ? L.T(s => s.PairingRoadmapDropped) : L.T(s => s.PairingRoadmapAdded),
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Color = Theme.Color(s => dropped ? s.Status.DangerText : s.Palette.Accent),
            });

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Start,
            Children = [.. children],
        };
    }
}

/// <summary>The conversation under the loop, read as the assistant chat is, with the agent's
/// thinking indicator while its next words are on the way.</summary>
internal sealed record PairingConversation : Widget
{
    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var loc = ctx.Localization();
        return new Column
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Each<PairingMessage>
                {
                    Items = store.Messages,
                    Template = new PairingMessageRow { Speaker = store.Harness },
                    Gap = Spacing.Lg,
                    CrossAxis = CrossAxisAlignment.Stretch,
                },
                new Show
                {
                    When = store.IsComposing,
                    Then = () => new AssistantThinkingIndicator
                    {
                        Label = Prop.Bind<string?>(() => loc.Strings.Value.PairingStatusThinking(store.Harness)),
                    },
                },
            ],
        };
    }
}

/// <summary>One entry of the conversation in the transcript row for its kind.</summary>
internal sealed record PairingMessageRow : Widget
{
    /// <summary>The agent's name, over its prose.</summary>
    public required string Speaker { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var message = ctx.Require<PairingMessage>();
        var loc = ctx.Localization();

        IWidget content = message switch
        {
            PairingMessage.FromUser said => new TranscriptMessageRow
            {
                Text = new State<string>(said.Text),
                Label = L.T(s => s.AssistantYou),
                LabelColor = static s => s.Palette.TextSecondary,
            },
            PairingMessage.Narration narration => new TranscriptReplyRow { Text = narration.Text, Speaker = Speaker },
            PairingMessage.Notice notice => new TranscriptNoticeRow
            {
                Text = new State<string>(notice.Text),
                Tone = notice.Tone switch
                {
                    NoticeTone.Info or NoticeTone.Refused => TranscriptNoticeTone.Advisory,
                    NoticeTone.Error => TranscriptNoticeTone.Error,
                    _ => throw new ArgumentOutOfRangeException(nameof(message), notice.Tone, "Unknown tone."),
                },
            },
            PairingMessage.Approval approval => new PairingApprovalCard { Pending = approval.Pending },
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, "Unknown message."),
        };

        return new FadeIn { Child = content };
    }
}

/// <summary>A tool call the write guard left to the user: what it is, and Deny / Approve.</summary>
internal sealed record PairingApprovalCard : Widget
{
    public required PendingToolApproval Pending { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var pending = Pending;
        var loc = ctx.Localization();
        return new Box
        {
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.BorderStrong)),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
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
                                    Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingApprovalTitle(pending.ToolName)),
                                    FontSize = FontSize.Body,
                                    Weight = FontWeight.Bold,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                                new Text
                                {
                                    Value = pending.Arguments,
                                    FontSize = FontSize.Caption,
                                    FontFamily = MonoFonts.Regular,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                                new ToolApprovalActions { Pending = pending },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
