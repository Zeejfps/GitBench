using GitBench.Controls;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// What the diff's quick actions do once a selection has become a question: which of them run
/// one-shot and which continue the repository's thread.
/// </summary>
public sealed class AssistantSelectionActionTests
{
    private static readonly IReadOnlyList<DiffRow> Rows =
    [
        new DiffRow.Line(
            DiffLineKind.Added, DiffGutterNumber.None, DiffGutterNumber.Of(new FileLine(42)),
            DiffLineText.Of("    Modern();")),
    ];

    private static DiffSelectionQuote Quote() =>
        DiffSelectionQuote.Build(
            Rows, new DiffTextPos(default, default), new DiffTextPos(default, new ExpandedColumn(13)),
            "src/Runner.cs")!;

    private static (List<AskAssistantAboutSelectionMessage> Asks, IReadOnlyList<RepoBarContextMenu.Item> Items) Menu()
    {
        var bus = new MessageBus();
        var asks = new List<AskAssistantAboutSelectionMessage>();
        bus.Subscribe<AskAssistantAboutSelectionMessage>(asks.Add);
        using var loc = new LocalizationService(new State<Locale>(Locale.En));
        return (asks, DiffAssistantMenu.Items(loc.Strings.Value, bus, Quote()));
    }

    [Fact]
    public void TheMenu_OffersThreePresetsAndAFreeFormAsk()
    {
        var (asks, items) = Menu();

        var actionable = items.Where(i => !i.IsSeparator).ToList();
        Assert.Equal(
            new[] { "Explain this", "What could break?", "Suggest a fix", "Ask…" },
            actionable.Select(i => i.Label));

        foreach (var item in actionable) item.OnSelected();

        Assert.Equal(
            new[]
            {
                AgentCatalog.ExplainSelectionAgent,
                AgentCatalog.BreakageSelectionAgent,
                AgentCatalog.FixSelectionAgent,
                null,
            },
            asks.Select(a => a.AgentName));
        Assert.All(asks, a => Assert.Contains("`src/Runner.cs`", a.Prompt, StringComparison.Ordinal));
        Assert.All(asks, a => Assert.Contains("added lines", a.Prompt, StringComparison.Ordinal));
        Assert.All(asks, a => Assert.Contains("    Modern();", a.Prompt, StringComparison.Ordinal));
    }

    // Every preset is a shipped .md, so the assertion that matters is that the catalog picks it up
    // with the tool list it declares — and that none of them can change anything.
    [Fact]
    public void ThePresetAgents_AreEmbeddedAndReadOnly()
    {
        var catalog = AgentCatalog.LoadEmbedded();

        foreach (var name in new[]
                 {
                     AgentCatalog.ExplainSelectionAgent,
                     AgentCatalog.BreakageSelectionAgent,
                     AgentCatalog.FixSelectionAgent,
                 })
        {
            var agent = catalog.Get(name);
            Assert.NotEmpty(agent.SystemPrompt);
            Assert.DoesNotContain("---", agent.SystemPrompt);
            Assert.DoesNotContain("mark_viewed", agent.AllowedTools);
            Assert.DoesNotContain("stage_files", agent.AllowedTools);
            Assert.DoesNotContain("commit", agent.AllowedTools);
            Assert.Contains("read_file", agent.AllowedTools);
        }
    }

    [Fact]
    public void APreset_OpensTheOverlayAndAnswersInTheTranscript()
    {
        using var fixture = new AssistantViewFixture(Answering("It calls the new implementation."));
        Assert.False(fixture.Vm.IsOpen.Value);

        Ask(fixture, AgentCatalog.ExplainSelectionAgent);

        Assert.True(fixture.Vm.IsOpen.Value);
        var rows = fixture.Vm.Session.Value!.Rows;
        Assert.Equal(AssistantRowKind.User, rows[0].Kind);
        Assert.Contains("src/Runner.cs", rows[0].Text.Value, StringComparison.Ordinal);
        Assert.Equal(AssistantRowKind.Reply, rows[1].Kind);
        Assert.Equal("It calls the new implementation.", rows[1].Text.Value);
    }

    [Fact]
    public void APreset_RunsAsItsOwnAgentRatherThanTheChatOne()
    {
        var backend = Answering("It calls the new implementation.");
        using var fixture = new AssistantViewFixture(backend);

        Ask(fixture, AgentCatalog.BreakageSelectionAgent);

        var expected = AgentCatalog.LoadEmbedded().Get(AgentCatalog.BreakageSelectionAgent).SystemPrompt;
        Assert.Equal(expected, Assert.Single(backend.Requests).SystemPrompt);
    }

    /// <summary>
    /// The detached property, asserted where it actually shows: the next ordinary message must not
    /// arrive with a one-shot about a fragment nobody is looking at any more folded underneath it.
    /// </summary>
    [Fact]
    public void APreset_DoesNotJoinTheRepositorysThread()
    {
        var backend = Answering("It calls the new implementation.", "Nothing else changed.");
        using var fixture = new AssistantViewFixture(backend);

        Ask(fixture, AgentCatalog.ExplainSelectionAgent);
        fixture.Ask("what else changed?");

        Assert.Equal(2, backend.Requests.Count);
        var second = backend.Requests[1];
        Assert.Single(second.Messages.OfType<AssistantMessage.User>());
        Assert.Equal("what else changed?", second.Messages.OfType<AssistantMessage.User>().Single().Text);
        Assert.DoesNotContain(
            second.Messages.OfType<AssistantMessage.User>(),
            m => m.Text.Contains("src/Runner.cs", StringComparison.Ordinal));
    }

    // "Ask…" is the opposite call: the quote lands in the composer, unsent, and whatever the person
    // writes under it goes to the thread like any other message.
    [Fact]
    public void TheFreeFormAsk_SeedsTheComposerWithoutSending()
    {
        var backend = Answering("Because the old one leaked.");
        using var fixture = new AssistantViewFixture(backend);

        Ask(fixture, agentName: null);

        Assert.True(fixture.Vm.IsOpen.Value);
        Assert.Contains("`src/Runner.cs`", fixture.Vm.Draft.Value, StringComparison.Ordinal);
        Assert.Contains("    Modern();", fixture.Vm.Draft.Value, StringComparison.Ordinal);
        Assert.Empty(backend.Requests);
        Assert.Empty(fixture.Vm.Session.Value!.Rows);
    }

    [Fact]
    public void TheFreeFormAsk_ContinuesTheConversation()
    {
        var backend = Answering("Because the old one leaked.", "It was removed in 3a1f2c.");
        using var fixture = new AssistantViewFixture(backend);

        Ask(fixture, agentName: null);
        fixture.Vm.SetDraft(fixture.Vm.Draft.Value + "why?");
        fixture.Vm.Send.Execute();
        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the seeded turn to finish");

        fixture.Ask("and where did it go?");

        var second = backend.Requests[1];
        var users = second.Messages.OfType<AssistantMessage.User>().Select(m => m.Text).ToList();
        Assert.Equal(2, users.Count);
        Assert.Contains("src/Runner.cs", users[0], StringComparison.Ordinal);
        Assert.Equal("and where did it go?", users[1]);
    }

    private static void Ask(AssistantViewFixture fixture, string? agentName)
    {
        fixture.Bus.Broadcast(new AskAssistantAboutSelectionMessage(agentName, Quote().ToPrompt(
            agentName is null ? null : "Explain this selection.")));
        if (agentName is null)
        {
            fixture.Frames();
            return;
        }

        Pump.WaitFor(fixture.Dispatcher, () => !fixture.Vm.IsBusy.Value, "the preset turn to finish");
        fixture.Frames();
    }

    private static FakeAssistantBackend Answering(params string[] answers) =>
        new(answers.Select(text => new BackendEvent[]
        {
            new BackendEvent.TextDelta(text),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        }).ToArray());
}
