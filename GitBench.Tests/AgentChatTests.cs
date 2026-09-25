using GitBench.App;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>The agent chat's way in: the first press asks which agent, the pick is remembered, and
/// later presses show and hide the conversation without ending it.</summary>
public sealed class AgentChatTests : IAsyncDisposable
{
    private readonly TempDir _dir = new("agent-chat");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly RepoRegistry _registry;
    private readonly PreferencesService _preferences;
    private readonly List<(PairingHarness Harness, AgentOpening Opening, AgentConversation Conversation)> _opened = new();
    private readonly PairingSessions _sessions;
    private readonly AgentChat _chat;

    public AgentChatTests()
    {
        var statePath = Path.Combine(_dir.Path, "state.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        var repo = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        _registry.Open(repo);
        _preferences = new PreferencesService(new Preferences(), Path.Combine(_dir.Path, "prefs.json"));
        _sessions = new PairingSessions(_registry, (repo, harness, opening) =>
        {
            var conversation = new AgentConversation(repo, harness, (_, _) => throw new InvalidOperationException("No sessions here."), _dispatcher);
            _opened.Add((harness, opening, conversation));
            return conversation;
        });
        _chat = new AgentChat(_sessions, _registry, _preferences, NoEndpoints(), new LocalizationService(new State<Locale>(Locale.En)));
    }

    public async ValueTask DisposeAsync()
    {
        _chat.Dispose();
        _sessions.Dispose();
        foreach (var (_, _, conversation) in _opened) await conversation.DisposeAsync();
        _preferences.Dispose();
        _dir.Dispose();
    }

    private static Features.AgentConnections.AgentEndpoints NoEndpoints() => new(
        new State<Features.AgentConnections.AgentConnectionSettings>(new(true, 5577, null)),
        new State<Features.AgentConnections.AgentConnectionState>(new Features.AgentConnections.AgentConnectionState.Off()),
        new QueuedDispatcher());

    private Guid RepoId => _registry.Active.Value!.Id;

    [Fact]
    public void TheFirstPress_AsksForAnAgent_AndOpensNothing()
    {
        Assert.IsType<AgentChatPress.NeedsAgent>(_chat.Press());
        Assert.Empty(_opened);
        Assert.Null(_sessions.Shown.Value);
    }

    [Fact]
    public void PickingAnAgent_OpensABlankConversationOnScreen_AndRemembersIt()
    {
        var conversation = _chat.Open(AcpHarness.Codex);

        var (harness, opening, opened) = Assert.Single(_opened);
        Assert.Same(opened, conversation);
        Assert.Equal(new PairingHarness.Acp(AcpHarness.Codex), harness);
        Assert.IsType<AgentOpening.Blank>(opening);
        Assert.Same(conversation, _sessions.Shown.Value);
        Assert.Equal(AcpHarness.Codex, _chat.Remembered);
    }

    [Fact]
    public void PressingAgain_HidesAndShowsTheConversation_WithoutEndingIt()
    {
        var conversation = _chat.Open(AcpHarness.ClaudeCode)!;

        Assert.IsType<AgentChatPress.Existing>(_chat.Press());
        Assert.Null(_sessions.Shown.Value);
        Assert.Same(conversation, _sessions.Active.Value);
        Assert.False(_sessions.IsPanelShown(RepoId));

        Assert.IsType<AgentChatPress.Existing>(_chat.Press());
        Assert.Same(conversation, _sessions.Shown.Value);
        Assert.Single(_opened);
    }

    [Fact]
    public void RevealingAHiddenConversation_ShowsIt_AndRevealingAgainKeepsItUp()
    {
        var conversation = _chat.Open(AcpHarness.ClaudeCode)!;
        _sessions.HidePanel(RepoId);

        _chat.Reveal();
        _chat.Reveal();

        Assert.Same(conversation, _sessions.Shown.Value);
    }

    [Fact]
    public void WithAnAgentRemembered_APress_OpensAConversationWithIt()
    {
        _preferences.Update(p => p with { ChatAgent = AcpHarness.Gemini.Id.Value });

        var opened = Assert.IsType<AgentChatPress.Opened>(_chat.Press());

        Assert.Equal(new PairingHarness.Acp(AcpHarness.Gemini), opened.Conversation.Harness);
    }

    [Fact]
    public void AskingInAHiddenConversation_PutsItBackOnScreen()
    {
        var conversation = _chat.Open(AcpHarness.ClaudeCode)!;
        _sessions.HidePanel(RepoId);

        _sessions.Ask(_registry.Active.Value!, "explain this", null, conversation.Harness);

        Assert.Same(conversation, _sessions.Shown.Value);
    }

    [Fact]
    public void PickingAnotherAgent_ReplacesTheConversation_AndTheMenuMarksIt()
    {
        var first = _chat.Open(AcpHarness.ClaudeCode)!;

        var second = _chat.Open(AcpHarness.Codex)!;

        Assert.NotSame(first, second);
        Assert.Same(second, _sessions.Shown.Value);
        var marked = Assert.Single(_chat.AgentMenu(), item => item.Checked);
        Assert.Equal(AcpHarness.Codex.Label, marked.Label);
    }

    [Fact]
    public void Restarting_OpensABlankConversationWithTheSameAgent_InPlaceOfTheOldOne()
    {
        var first = _chat.Open(AcpHarness.ClaudeCode)!;
        first.Say("remember this");
        _sessions.HidePanel(RepoId);

        var restarted = _sessions.Restart(RepoId);

        Assert.NotNull(restarted);
        Assert.NotSame(first, restarted);
        Assert.Equal(first.Harness, restarted.Harness);
        Assert.IsType<AgentOpening.Blank>(_opened[^1].Opening);
        Assert.Empty(restarted.Transcript.Messages);
        Assert.Same(restarted, _sessions.Shown.Value);
    }

    [Fact]
    public void Restarting_WithNoConversation_OpensNothing()
    {
        Assert.Null(_sessions.Restart(RepoId));
        Assert.Empty(_opened);
    }

    [Fact]
    public void AskingWithNoAgentPicked_AsksForOne_AndOpensNothing()
    {
        Assert.IsType<AgentChatAsk.NeedsAgent>(_chat.Ask("Explain this selection.", null));
        Assert.Empty(_opened);
    }

    [Fact]
    public void AskingWithAnAgentRemembered_OpensAConversationOnTheQuestion()
    {
        _preferences.Update(p => p with { ChatAgent = AcpHarness.Codex.Id.Value });

        var asked = Assert.IsType<AgentChatAsk.Asked>(_chat.Ask("What could break here?", null));

        var (harness, opening, conversation) = Assert.Single(_opened);
        Assert.Same(conversation, asked.Conversation);
        Assert.Equal(new PairingHarness.Acp(AcpHarness.Codex), harness);
        Assert.Equal("What could break here?", Assert.IsType<AgentOpening.Chat>(opening).Text);
        Assert.Same(conversation, _sessions.Shown.Value);
    }

    [Fact]
    public void AskingWhileAConversationIsHidden_SaysItThere_AndShowsIt()
    {
        var conversation = _chat.Open(AcpHarness.ClaudeCode)!;
        conversation.MarkRunning();
        _sessions.HidePanel(RepoId);

        var asked = Assert.IsType<AgentChatAsk.Asked>(_chat.Ask("Suggest a fix for this.", null));

        Assert.Same(conversation, asked.Conversation);
        Assert.Single(_opened);
        var said = Assert.IsType<PairingMessage.FromUser>(conversation.Transcript.Messages[^1]);
        Assert.Equal("Suggest a fix for this.", said.Text);
        Assert.Same(conversation, _sessions.Shown.Value);
    }

    [Fact]
    public void PickingFromTheMenu_CarriesOnWithWhatWasAsked()
    {
        AgentConversation? carried = null;

        var codex = Assert.Single(_chat.AgentMenu(conversation => carried = conversation), i => i.Label == AcpHarness.Codex.Label);
        codex.OnSelected();

        Assert.NotNull(carried);
        Assert.Same(carried, _sessions.Shown.Value);
    }

    [Fact]
    public void PickingTheSameAgent_KeepsTheConversation()
    {
        var first = _chat.Open(AcpHarness.ClaudeCode)!;
        _sessions.HidePanel(RepoId);

        Assert.Same(first, _chat.Open(AcpHarness.ClaudeCode));
        Assert.Same(first, _sessions.Shown.Value);
        Assert.Single(_opened);
    }
}
