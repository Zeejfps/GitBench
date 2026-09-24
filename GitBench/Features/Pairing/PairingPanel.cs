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
/// screen has a conversation with an agent, gone otherwise.</summary>
internal sealed record PairingPanelSlot : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var sessions = ctx.Require<PairingSessions>();
        var preferences = ctx.Require<PreferencesService>();
        return new Switch<AgentConversation?>
        {
            Value = sessions.Active,
            Case = conversation => conversation is null
                ? Empty.Widget
                : new ResizableSidebar
                {
                    Edge = SidebarEdge.Trailing,
                    InitialWidth = preferences.Current.PairingPanelWidth,
                    MinResizeWidth = 280f,
                    OnWidthChanged = w => preferences.Update(p => p with { PairingPanelWidth = w }),
                    Content = new PairingPanel { Conversation = conversation },
                },
        };
    }
}

/// <summary>
/// The conversation with a repository's agent, the way the user follows it: who drives and whose
/// turn it is; while a pairing session runs, the stop they are on and the goal and roadmap with what
/// the agent last changed in it; and the conversation under it with a field to talk to the agent,
/// which stays once the session is over.
/// </summary>
internal sealed record PairingPanel : Widget
{
    public const string PanelId = "pairing-panel";

    public required AgentConversation Conversation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var conversation = Conversation;
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
                        new PairingHeader { Conversation = conversation },
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
                                                new Switch<PairingSession?>
                                                {
                                                    Value = conversation.Session,
                                                    Case = session => session is null
                                                        ? Empty.Widget
                                                        : new Column
                                                        {
                                                            Gap = Spacing.Lg,
                                                            CrossAxis = CrossAxisAlignment.Stretch,
                                                            Children =
                                                            [
                                                                new PairingStopSlot { Store = session.Store },
                                                                new PairingGoalAndRoadmap { Store = session.Store },
                                                            ],
                                                        },
                                                },
                                                new PairingConversation { Conversation = conversation },
                                            ],
                                        },
                                    ],
                                },
                            },
                        },
                        new PairingChatFooter { Conversation = conversation },
                        new Switch<PairingSession?>
                        {
                            Value = conversation.Session,
                            Case = session => session is null
                                ? Empty.Widget
                                : new Show
                                {
                                    When = new Derived<bool>(() => session.Store.IsLive),
                                    Then = () => new PairingStopActions { Store = session.Store },
                                },
                        },
                    ],
                },
            ],
        };

        return panel.Use(_ => Follow(conversation, scroll, ctx.Require<IUiDispatcher>()));
    }

    // What the user says pins the panel to the end of the conversation, which then keeps up with
    // the reply as it streams in; a new stop takes the panel back to the top, where its card is.
    private static IDisposable Follow(AgentConversation conversation, ScrollRegionHandle scroll, IUiDispatcher dispatcher)
    {
        var subscriptions = new SubscriptionGroup();
        var messages = conversation.Transcript.Messages;
        var count = messages.Count;
        subscriptions.Add(messages.Subscribe(_ =>
        {
            var grew = messages.Count > count;
            count = messages.Count;
            if (grew && messages[^1] is PairingMessage.FromUser or PairingMessage.SessionOver) scroll.FollowBottom();
        }));

        var stop = new Derived<int>(() => conversation.Session.Value?.Store.Stop.Value?.Stop.Number ?? 0);
        subscriptions.Add(stop);
        var stopNumber = stop.Value;
        subscriptions.Add(stop.Subscribe(number =>
        {
            if (number == stopNumber) return;
            stopNumber = number;
            // Posted, so the card is in the tree before the region goes to it.
            if (number != 0) dispatcher.Post(scroll.ScrollToTop);
        }));
        return subscriptions;
    }
}

/// <summary>The panel's top band: the agent, whose turn it is, End while a session runs, and Close,
/// which ends the conversation and stops the agent.</summary>
internal sealed record PairingHeader : Widget
{
    public const string EndId = "pairing-end";
    public const string CloseId = "pairing-close";

    public required AgentConversation Conversation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        var conversation = Conversation;
        var sessions = ctx.Require<PairingSessions>();
        var pairing = new Derived<bool>(() => conversation.IsPairing);

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
                                    Value = conversation.Harness.Label,
                                    FontSize = FontSize.Body,
                                    Weight = FontWeight.Bold,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                    VAlign = TextAlignment.Center,
                                },
                                new Grow
                                {
                                    Child = new Text
                                    {
                                        Value = Prop.Bind<string?>(() => Status(loc.Strings.Value, conversation)),
                                        FontSize = FontSize.Caption,
                                        Color = Prop.Bind(() => IsTrouble(conversation)
                                            ? ctx.Theme().Styles.Value.Status.DangerText
                                            : ctx.Theme().Styles.Value.Palette.TextSecondary),
                                        VAlign = TextAlignment.Center,
                                        Overflow = TextOverflow.Ellipsis,
                                    },
                                },
                                new Show
                                {
                                    When = pairing,
                                    Then = () => new ButtonWidget
                                    {
                                        Id = EndId,
                                        Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                        Command = new Command(() => conversation.Session.Value?.Store.EndByUser()),
                                        Children = [new ButtonLabel { Value = L.T(s => s.PairingEnd) }],
                                    }.WithController<KbmController>(),
                                },
                                new ButtonWidget
                                {
                                    Id = CloseId,
                                    Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                    Command = new Command(() => sessions.Close(conversation.Repo.Id)),
                                    Children = [new ButtonLabel { Value = L.T(s => s.PairingClose) }],
                                }.WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        }.Use(_ => pairing);
    }

    private static bool IsTrouble(AgentConversation conversation) =>
        conversation.Phase.Value is AgentPhase.Gone
        || conversation.Session.Value?.Store.Phase.Value is PairingPhase.Failed or PairingPhase.Disconnected;

    private static string Status(Strings s, AgentConversation conversation)
    {
        var agent = conversation.Harness.Label;
        if (conversation.Session.Value is { } session)
            return session.Store.Phase.Value switch
            {
                PairingPhase.Starting => s.PairingStatusStarting(agent),
                PairingPhase.Running { Waiting: true } => s.PairingStatusYourTurn,
                PairingPhase.Running => s.PairingStatusThinking(agent),
                PairingPhase.Ended => s.PairingStatusEnded,
                PairingPhase.Failed => s.PairingStatusFailed,
                PairingPhase.Disconnected => s.PairingStatusDisconnected,
                _ => throw new InvalidOperationException("Unknown phase."),
            };

        return conversation.Phase.Value switch
        {
            AgentPhase.Starting => s.PairingStatusStarting(agent),
            AgentPhase.Running { Busy: true } => s.PairingStatusThinking(agent),
            AgentPhase.Running => s.PairingStatusReady,
            AgentPhase.Gone => s.PairingStatusDisconnected,
            _ => throw new InvalidOperationException("Unknown agent phase."),
        };
    }
}

/// <summary>How a session ended, in the conversation where it did: the agent's summary, or why it
/// failed.</summary>
internal sealed record PairingOutcome : Widget
{
    public required PairingPhase Outcome { get; init; }

    protected override IWidget Build(Context ctx) => Outcome switch
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
        PairingPhase.Ended => new TranscriptNoticeRow { Text = new Derived<string>(() => ctx.Localization().Strings.Value.PairingStatusEnded), Tone = TranscriptNoticeTone.Advisory },
        PairingPhase.Failed failed => new TranscriptNoticeRow { Text = new State<string>(failed.Reason), Tone = TranscriptNoticeTone.Error },
        PairingPhase.Disconnected gone => new TranscriptNoticeRow { Text = new State<string>(gone.Reason), Tone = TranscriptNoticeTone.Advisory },
        PairingPhase.Starting or PairingPhase.Running => throw new ArgumentException("A live session has no outcome.", nameof(Outcome)),
        _ => throw new ArgumentOutOfRangeException(nameof(Outcome), Outcome, "Unknown phase."),
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

/// <summary>The conversation with the agent, read as the assistant chat is, with the agent's
/// thinking indicator while its next words are on the way.</summary>
internal sealed record PairingConversation : Widget
{
    public required AgentConversation Conversation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var conversation = Conversation;
        var loc = ctx.Localization();
        return new Column
        {
            Gap = Spacing.Lg,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Each<PairingMessage>
                {
                    Items = conversation.Transcript.Messages,
                    Template = new PairingMessageRow { Speaker = conversation.Harness.Label, RepoPath = conversation.Repo.Path },
                    Gap = Spacing.Lg,
                    CrossAxis = CrossAxisAlignment.Stretch,
                },
                new Show
                {
                    When = conversation.IsComposing,
                    Then = () => new AssistantThinkingIndicator
                    {
                        Label = Prop.Bind<string?>(() => loc.Strings.Value.PairingStatusThinking(conversation.Harness.Label)),
                    },
                },
            ],
        };
    }
}

/// <summary>Under the conversation: the field to talk to the agent while what is typed there reaches
/// it, and otherwise, for an agent in a terminal, where to talk to it instead.</summary>
internal sealed record PairingChatFooter : Widget
{
    public required AgentConversation Conversation { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var conversation = Conversation;
        var loc = ctx.Localization();
        return new Switch<bool>
        {
            Value = conversation.TakesChat,
            Case = takes => takes
                ? new PairingAskField { Conversation = conversation }
                : conversation.Harness is PairingHarness.Terminal
                    ? new Show
                    {
                        When = new Derived<bool>(() => !conversation.IsGone),
                        Then = () => new Padding
                        {
                            Amount = PaddingStyle.All(Spacing.Md),
                            Children =
                            [
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingTalkInTerminal(conversation.Harness.Label)),
                                    FontSize = FontSize.Caption,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                },
                            ],
                        },
                    }
                    : Empty.Widget,
        };
    }
}

/// <summary>One entry of the conversation in the transcript row for its kind.</summary>
internal sealed record PairingMessageRow : Widget
{
    /// <summary>The agent's name, over its prose.</summary>
    public required string Speaker { get; init; }

    /// <summary>The repository, whose paths quotes are shown relative to.</summary>
    public required string RepoPath { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var message = ctx.Require<PairingMessage>();
        var loc = ctx.Localization();

        IWidget content = message switch
        {
            PairingMessage.SessionStarted started => new Text
            {
                Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingSessionStarted(started.Goal)),
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Wrap = TextWrap.Wrap,
                Color = Theme.Color(s => s.Palette.TextSecondary),
            },
            PairingMessage.SessionOver over => new PairingOutcome { Outcome = over.Outcome },
            PairingMessage.FromUser { Quote: null } said => new TranscriptMessageRow
            {
                Text = new State<string>(said.Text),
                Label = L.T(s => s.AssistantYou),
                LabelColor = static s => s.Palette.TextSecondary,
            },
            PairingMessage.FromUser { Quote: { } quote } said => new Column
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new TranscriptMessageRow
                    {
                        Text = new State<string>(said.Text),
                        Label = L.T(s => s.AssistantYou),
                        LabelColor = static s => s.Palette.TextSecondary,
                    },
                    new PairingQuoteCard { Quote = quote, Location = quote.Location(path => AgentPrompt.RepoRelative(RepoPath, path)) },
                ],
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
