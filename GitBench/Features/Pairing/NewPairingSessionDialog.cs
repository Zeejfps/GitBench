using GitBench.App;
using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.AgentConnections;
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

/// <summary>An agent a session can be started with, as the dialog offers them.</summary>
internal abstract record PairingAgentChoice
{
    public sealed record Preset(AgentPreset Value) : PairingAgentChoice;

    public sealed record Terminal : PairingAgentChoice;
}

/// <summary>
/// Starts a pairing session on the active repository: the goal in the user's words and the agent
/// that navigates it. The session opens in the Pairing panel and the Files pane.
/// </summary>
internal sealed record NewPairingSessionDialog : Widget
{
    public const string GoalId = "pairing-goal";

    public required Action OnClose { get; init; }

    public static void Show(IMessageBus bus) =>
        bus.Broadcast(new ShowDialogMessage(onClose => new NewPairingSessionDialog { OnClose = onClose }));

    protected override IWidget Build(Context ctx)
    {
        var onClose = OnClose;
        var sessions = ctx.Require<PairingSessions>();
        var repos = ctx.Require<IRepoRegistry>();
        var endpoints = ctx.Require<AgentEndpoints>();
        var mode = ctx.Require<State<MainViewMode>>();
        var loc = ctx.Require<ILocalizationService>();
        var s = loc.Strings.Value;

        var goal = new State<string>(string.Empty);
        var agent = new State<PairingAgentChoice>(new PairingAgentChoice.Preset(ctx.Require<AgentChat>().Default));
        var preferences = ctx.Require<PreferencesService>();
        var terminalCommand = new State<string>(preferences.Current.PairingTerminalCommand);
        var error = new State<string?>(repos.Active.Value is null ? s.PairingNoRepo : null);
        // A conversation already under way keeps its agent, and with it what it knows.
        var ongoing = repos.Active.Value is { } activeRepo ? sessions.LiveConversation(activeRepo.Id) : null;
        var canStart = new Derived<bool>(() => goal.Value.Trim().Length > 0 && repos.Active.Value is not null
            && (ongoing is not null || agent.Value is not PairingAgentChoice.Terminal || terminalCommand.Value.Trim().Length > 0));

        void Start()
        {
            if (repos.Active.Value is not { } repo || goal.Value.Trim().Length == 0) return;
            PairingHarness harness = ongoing?.Harness ?? agent.Value switch
            {
                PairingAgentChoice.Terminal => new PairingHarness.Terminal(loc.Strings.Value.PairingAgentTerminal, terminalCommand.Value.Trim()),
                PairingAgentChoice.Preset preset => new PairingHarness.Acp(preset.Value),
                _ => throw new InvalidOperationException("Unhandled agent choice."),
            };
            switch (sessions.Start(repo, goal.Value, harness))
            {
                case PairingStart.Started:
                    if (ongoing is null)
                        preferences.Update(p => p with
                        {
                            PairingTerminalCommand = agent.Value is PairingAgentChoice.Terminal ? terminalCommand.Value.Trim() : p.PairingTerminalCommand,
                        });
                    mode.Value = MainViewMode.Files;
                    onClose();
                    break;
                case PairingStart.AlreadyRunning:
                    error.Value = loc.Strings.Value.PairingAlreadyRunning;
                    break;
                default:
                    throw new InvalidOperationException("Unhandled pairing start.");
            }
        }

        var goalField = new GrowingDescriptionField(ctx, 72f, 200f) { Id = GoalId, PlaceholderText = s.PairingGoalPlaceholder, AutoFocus = true };
        goalField.BindTwoWay(goal, v => goal.Value = v);

        List<IWidget> body =
        [
            new DialogBodyText { Value = s.PairingNewSessionDesc },
            new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new Text { Value = s.PairingGoal, Color = Theme.Color(t => t.DialogBody.SectionHeaderText) },
                    new Raw { View = goalField },
                ],
            },
        ];
        if (ongoing is not null)
            body.Add(new DialogBodyText { Value = s.PairingContinuesWith(ongoing.Harness.Label) });
        else
            body.AddRange(
            [
                new LabeledRow
                {
                    Label = s.PairingAgent,
                    Value = new OptionDropdown<PairingAgentChoice>
                    {
                        Selected = agent,
                        Options =
                        [
                            .. PresetOptions(ctx.Require<AgentChat>().Presets, s),
                            (new PairingAgentChoice.Terminal(), s.PairingAgentTerminal, s.PairingAgentTerminalDetail),
                        ],
                    },
                },
                new Show
                {
                    When = new Derived<bool>(() => agent.Value is PairingAgentChoice.Terminal),
                    Then = () => new Column
                    {
                        Gap = Spacing.Sm,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new LabeledInput
                            {
                                Label = s.PairingTerminalCommand,
                                Value = terminalCommand,
                                Hint = s.PairingTerminalCommandHint(prompt: "{prompt}", promptFile: "{promptFile}", mcpUrl: "{mcpUrl}", mcpConfigFile: "{mcpConfigFile}", cwd: "{cwd}"),
                            },
                            new DialogBodyText { Value = s.PairingNoWriteGuard },
                        ],
                    },
                },
            ]);
        if (endpoints.WillEnable) body.Add(new DialogBodyText { Value = s.PairingEnablesConnections });

        return new Dialog
        {
            Title = s.PairingNewSession,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.PairingStart, DialogButtonRole.Primary, Start),
            ActionEnabled = canStart,
            InlineError = error,
            Body = [.. body],
        };
    }

    public static IEnumerable<(PairingAgentChoice, string, string)> PresetOptions(IReadOnlyList<AgentPreset> presets, Strings s) =>
        presets.Select(p => ((PairingAgentChoice)new PairingAgentChoice.Preset(p), p.Name, AgentKinds.Detail(p.Kind, s)));
}
