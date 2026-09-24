using GitBench.App;
using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Editor;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// Sends code the user selected to the agent with what they want to know or have done about it:
/// into the repository's conversation while one is open, or a new one with the agent picked here.
/// </summary>
internal sealed record SendToAgentDialog : Widget
{
    public const string QuestionId = "send-to-agent-question";

    public required CodeQuote Quote { get; init; }

    /// <summary>What the field starts with.</summary>
    public string Question { get; init; } = string.Empty;

    public required Action OnClose { get; init; }

    public static void Show(IMessageBus bus, CodeQuote quote, string question = "") =>
        bus.Broadcast(new ShowDialogMessage(onClose => new SendToAgentDialog { Quote = quote, Question = question, OnClose = onClose }));

    /// <summary>Asks the repository's agent about the code straight away, or, with no agent to ask
    /// yet, puts the question in this dialog to pick one.</summary>
    public static void Ask(IMessageBus bus, AgentChat chat, CodeQuote quote, string question)
    {
        if (chat.Ask(question, quote) is AgentChatAsk.NeedsAgent) Show(bus, quote, question);
    }

    protected override IWidget Build(Context ctx)
    {
        var quote = Quote;
        var onClose = OnClose;
        var sessions = ctx.Require<PairingSessions>();
        var chat = ctx.Require<AgentChat>();
        var preferences = ctx.Require<PreferencesService>();
        var repos = ctx.Require<IRepoRegistry>();
        var endpoints = ctx.Require<AgentEndpoints>();
        var s = ctx.Require<ILocalizationService>().Strings.Value;

        var repo = repos.Active.Value;
        var ongoing = repo is null ? null : sessions.LiveConversation(repo.Id);
        var question = new State<string>(Question);
        var agent = new State<PairingAgentChoice>(ChoiceOf(chat.Remembered));
        var error = new State<string?>(repo is null ? s.PairingNoRepo : null);
        var canSend = new Derived<bool>(() => question.Value.Trim().Length > 0 && repos.Active.Value is not null);

        void Send()
        {
            if (repos.Active.Value is not { } target || question.Value.Trim().Length == 0) return;
            var harness = NewPairingSessionDialog.HarnessOf(agent.Value);
            if (ongoing is null) preferences.Update(p => p with { ChatAgent = harness.Id.Value });
            sessions.Ask(target, question.Value, quote, ongoing?.Harness ?? new PairingHarness.Acp(harness));
            onClose();
        }

        var field = new GrowingDescriptionField(ctx, 72f, 200f) { Id = QuestionId, PlaceholderText = s.AgentSendPlaceholder };
        field.BindTwoWay(question, v => question.Value = v);
        var location = quote.Location(path => repo is null ? path : AgentPrompt.RepoRelative(repo.Path, path));

        List<IWidget> body =
        [
            new PairingQuoteCard { Quote = quote, Location = location },
            new Raw { View = field },
        ];
        body.Add(ongoing is not null
            ? new DialogBodyText { Value = s.AgentSendContinues(ongoing.Harness.Label) }
            : new LabeledRow
            {
                Label = s.PairingAgent,
                Value = new OptionDropdown<PairingAgentChoice>
                {
                    Selected = agent,
                    Options =
                    [
                        (PairingAgentChoice.ClaudeCode, AcpHarness.ClaudeCode.Label, s.PairingAgentClaudeDetail),
                        (PairingAgentChoice.Codex, AcpHarness.Codex.Label, s.PairingAgentCodexDetail),
                        (PairingAgentChoice.Gemini, AcpHarness.Gemini.Label, s.PairingAgentGeminiDetail),
                    ],
                },
            });
        if (ongoing is null && endpoints.WillEnable) body.Add(new DialogBodyText { Value = s.PairingEnablesConnections });

        return new Dialog
        {
            Title = s.AgentSendTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.AgentSend, DialogButtonRole.Primary, Send),
            ActionEnabled = canSend,
            InlineError = error,
            Body = [.. body],
        };
    }

    private static PairingAgentChoice ChoiceOf(AcpHarness? harness) =>
        harness?.Id == AcpHarness.Codex.Id ? PairingAgentChoice.Codex
        : harness?.Id == AcpHarness.Gemini.Id ? PairingAgentChoice.Gemini
        : PairingAgentChoice.ClaudeCode;
}

/// <summary>Code sent to the agent, as the user sees it went: where it is, then the first lines of it.</summary>
internal sealed record PairingQuoteCard : Widget
{
    private const int ShownLines = 8;

    public required CodeQuote Quote { get; init; }

    /// <summary>Where the code is, as the agent is told it.</summary>
    public required string Location { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var lines = Quote.Text.Split('\n');
        var shown = string.Join('\n', lines.Take(ShownLines).Select(l => l.TrimEnd('\r')));
        if (lines.Length > ShownLines) shown += "\n…";
        return new Box
        {
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
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
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Text
                                {
                                    Value = Location,
                                    FontSize = FontSize.Caption,
                                    Weight = FontWeight.Bold,
                                    Overflow = TextOverflow.Ellipsis,
                                    Color = Theme.Color(s => s.Palette.TextSecondary),
                                },
                                new Text
                                {
                                    Value = shown,
                                    FontSize = FontSize.Caption,
                                    FontFamily = MonoFonts.Regular,
                                    Wrap = TextWrap.Wrap,
                                    Color = Theme.Color(s => s.Palette.TextBody),
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
