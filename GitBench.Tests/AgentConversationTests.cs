using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Pairing;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>The conversation around pairing sessions: it outlives each session, so the user can go
/// on talking to the agent — to commit, to push, to start the next session — and what they say goes
/// to the loop while a session runs and straight to the agent between sessions.</summary>
public sealed class AgentConversationTests : IAsyncDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly RecordingDriver _driver = new();
    private readonly Repo _repo = new(Guid.NewGuid(), "C:/repo", "repo");
    private readonly List<AgentConversation> _conversations = new();

    private AgentConversation Create(PairingHarness? harness = null)
    {
        var conversation = new AgentConversation(_repo, harness ?? new PairingHarness.Acp(AcpHarness.ClaudeCode), (goal, transcript) =>
        {
            var presentation = new RecordingPairingPresentation();
            var store = new PairingStore(goal, "Claude Code", transcript, presentation, new ScriptedWorkspace(), _dispatcher, _clock);
            return new PairingSession(_repo, store, new NoDisposal());
        }, _dispatcher);
        conversation.Attach(_driver);
        _conversations.Add(conversation);
        return conversation;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var conversation in _conversations) await conversation.DisposeAsync();
    }

    [Fact]
    public void EndingTheSession_KeepsTheConversation_WithHowItEndedInTheTranscript()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();
        var store = conversation.Session.Value!.Store;

        store.End("Retry added.");
        _dispatcher.Drain();

        Assert.Null(conversation.Session.Value);
        Assert.IsType<AgentPhase.Running>(conversation.Phase.Value);
        var over = Assert.IsType<PairingMessage.SessionOver>(conversation.Transcript.Messages[^1]);
        Assert.Equal(new PairingPhase.Ended("Retry added."), over.Outcome);
        Assert.True(conversation.TakesChat.Value);
    }

    [Fact]
    public void TheUserEndingTheSession_StillTellsTheWaitingAgentItEnded()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();
        var wait = conversation.Session.Value!.Store.WaitAsync(CancellationToken.None);

        conversation.Session.Value!.Store.EndByUser();
        _dispatcher.Drain();

        Assert.IsType<PairingAction.Ended>(wait.Result);
        Assert.Null(conversation.Session.Value);
    }

    [Fact]
    public void BetweenSessions_WhatTheUserSays_GoesStraightToTheAgent()
    {
        var conversation = Create();
        conversation.MarkRunning();

        conversation.Say("  commit what we did ");

        Assert.Equal([new AgentPrompt("commit what we did")], _driver.Told);
        Assert.IsType<PairingMessage.FromUser>(Assert.Single(conversation.Transcript.Messages));
    }

    [Fact]
    public void DuringASession_WhatTheUserSays_GoesThroughTheLoop()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();

        conversation.Say("Why here?");

        Assert.Empty(_driver.Told);
        var said = Assert.IsType<PairingAction.Message>(conversation.Session.Value!.Store.WaitAsync(CancellationToken.None).Result);
        Assert.Equal("Why here?", said.Text);
    }

    [Fact]
    public void TheUserStartingASessionLater_TellsTheAgentTheGoal()
    {
        var conversation = Create();
        conversation.MarkRunning();

        var session = conversation.BeginSession("Rename the client");

        Assert.True(session.Store.IsLive);
        Assert.IsType<PairingPhase.Running>(session.Store.Phase.Value);
        Assert.Contains("Rename the client", Assert.Single(_driver.Told).Text);
        Assert.Equal(new PairingMessage.SessionStarted("Rename the client"), conversation.Transcript.Messages[^1]);
    }

    [Fact]
    public void MovingOnFromAStop_KeepsWhatWasSaidBeforeTheSession()
    {
        var conversation = Create();
        conversation.MarkRunning();
        conversation.Say("Let's pair on the retry");
        var store = conversation.StartSession("Add a retry").Store;
        store.Say("Skip this");

        conversation.Transcript.AppendNarration("Sure.");
        _ = store.OpenStopAsync(new StopTarget("src/Client.cs", "Fetch", null), "Retry", "Because.",
            new DraftRequest("x", new DraftSpan.Declaration()), false, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => store.Stop.Value is not null, "the stop to open");
        store.Skip();

        Assert.Equal(
            [typeof(PairingMessage.FromUser), typeof(PairingMessage.SessionStarted)],
            conversation.Transcript.Messages.Select(m => m.GetType()));
    }

    [Fact]
    public void TheAgentDying_FailsTheSession_AndClosesTheChat()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();

        conversation.Fail("Claude Code exited.");
        _dispatcher.Drain();

        Assert.IsType<AgentPhase.Gone>(conversation.Phase.Value);
        var over = Assert.IsType<PairingMessage.SessionOver>(conversation.Transcript.Messages[^1]);
        Assert.Equal(new PairingPhase.Failed("Claude Code exited."), over.Outcome);
        Assert.False(conversation.TakesChat.Value);
    }

    [Fact]
    public void ATerminalAgent_IsTalkedToInItsTerminal_BetweenSessions_ButStillTakesWhatTheEditorSends()
    {
        var conversation = Create(new PairingHarness.Terminal("Terminal agent", "claude {prompt}"));
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();
        Assert.True(conversation.TakesChat.Value);

        conversation.Session.Value!.Store.End(null);
        _dispatcher.Drain();

        Assert.False(conversation.TakesChat.Value);
        conversation.Say("What does this do?", Quote);
        Assert.Equal([new AgentPrompt("What does this do?", Quote)], _driver.Told);
    }

    private static readonly CodeQuote Quote = new CodeQuote.InFile("C:/repo/src/Client.cs", new FileLine(10), new FileLine(12), "void Fetch()\n{\n}");

    [Fact]
    public void CodeSentBetweenSessions_GoesToTheAgentWithItsQuote_AndShowsInTheTranscript()
    {
        var conversation = Create();
        conversation.MarkRunning();

        conversation.Say("Why is this sync?", Quote);

        var told = Assert.Single(_driver.Told);
        Assert.Same(Quote, told.Quote);
        Assert.Contains("`src/Client.cs`, lines 10-12", told.ToMarkdown("C:/repo"));
        Assert.Same(Quote, Assert.IsType<PairingMessage.FromUser>(Assert.Single(conversation.Transcript.Messages)).Quote);
    }

    [Fact]
    public void CodeSentDuringASession_ReachesTheLoopAsPartOfTheMessage()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();

        conversation.Say("Should this retry too?", Quote);

        var said = Assert.IsType<PairingAction.Message>(conversation.Session.Value!.Store.WaitAsync(CancellationToken.None).Result);
        Assert.StartsWith("Should this retry too?", said.Text);
        Assert.Contains("void Fetch()", said.Text);
        Assert.Contains("`src/Client.cs`, lines 10-12", said.Text);
    }

    [Fact]
    public void StartingASessionWhileOneRuns_IsRefused()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");

        Assert.Throws<InvalidOperationException>(() => conversation.StartSession("Something else"));
    }

    private sealed class RecordingDriver : IAgentDriver
    {
        public List<AgentPrompt> Told { get; } = new();

        public void Tell(AgentPrompt prompt) => Told.Add(prompt);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoDisposal : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
