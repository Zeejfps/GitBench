using GitBench.Features.AgentConnections;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Pairing;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>The conversation around pairing sessions: it outlives each session, so the user can go
/// on talking to the agent — to commit, to push, to start the next session — and what they say goes
/// to the agent as a move of the session while one runs, and as it is between sessions.</summary>
public sealed class AgentConversationTests : IAsyncDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly RecordingDriver _driver = new();
    private readonly Repo _repo = new(Guid.NewGuid(), "C:/repo", "repo");
    private readonly List<AgentConversation> _conversations = new();

    private AgentConversation Create(PairingHarness? harness = null)
    {
        var conversation = new AgentConversation(_repo, harness ?? new PairingHarness.Acp(AgentPreset.ClaudeCode), (goal, transcript, deliver) =>
        {
            var presentation = new RecordingPairingPresentation();
            var store = new PairingStore(goal, "Claude Code", transcript, presentation, new ScriptedWorkspace(), _dispatcher, deliver);
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
    public void TheUserEndingTheSession_TellsTheAgentItEnded()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();

        conversation.Session.Value!.Store.EndByUser();
        _dispatcher.Drain();

        Assert.Contains("ended the session", Assert.Single(_driver.Told).Text);
        Assert.Null(conversation.Session.Value);
    }

    [Fact]
    public void TheAgentsTurnEnding_IsTheUsersTurnInTheSession()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();
        var store = conversation.Session.Value!.Store;

        conversation.BeginTurn();
        Assert.Equal(new PairingPhase.Running(false), store.Phase.Value);
        Assert.True(conversation.IsComposing.Value);

        conversation.EndTurn();

        Assert.Equal(new PairingPhase.Running(true), store.Phase.Value);
        Assert.False(conversation.IsComposing.Value);
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
    public void DuringASession_WhatTheUserSays_ReachesTheAgentAsAMoveOfTheSession()
    {
        var conversation = Create();
        conversation.StartSession("Add a retry");
        conversation.MarkRunning();

        conversation.Say("Why here?");

        var told = Assert.Single(_driver.Told).Text;
        Assert.StartsWith("DiffDino pairing:", told);
        Assert.EndsWith("Why here?", told);
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
    public void ProseAfterAToolCall_StartsAParagraphOfItsOwn()
    {
        var transcript = new AgentTranscript();
        transcript.AppendNarration("```cs\nvar x = 1;\n```");
        transcript.BreakNarration();
        transcript.AppendNarration("Now the tests.");

        var narration = Assert.IsType<PairingMessage.Narration>(Assert.Single(transcript.Messages));
        Assert.Equal("```cs\nvar x = 1;\n```\n\nNow the tests.", narration.Text.Value);
    }

    [Fact]
    public void ProseAfterAReply_IsNotShown_UntilTheNextTurn()
    {
        var transcript = new AgentTranscript();
        transcript.BeginAgentTurn();
        transcript.AppendNarration("Looking at the hook.");
        transcript.AddReply("OK, leaving it out.");
        transcript.AppendNarration("OK, I've left it out.");
        transcript.CloseNarration();
        transcript.BeginAgentTurn();
        transcript.AppendNarration("Next stop is open.");

        Assert.Equal(
            ["Looking at the hook.", "OK, leaving it out.", "Next stop is open."],
            transcript.Messages.Select(m => Assert.IsType<PairingMessage.Narration>(m).Text.Value));
    }

    [Fact]
    public void ABreakBeforeAnyProse_LeavesTheNextMessageAsItIs()
    {
        var transcript = new AgentTranscript();
        transcript.BreakNarration();
        transcript.AppendNarration("Looking.");

        var narration = Assert.IsType<PairingMessage.Narration>(Assert.Single(transcript.Messages));
        Assert.Equal("Looking.", narration.Text.Value);
    }

    [Fact]
    public void MovingOnFromAStop_KeepsTheWholeConversation()
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
            [typeof(PairingMessage.FromUser), typeof(PairingMessage.SessionStarted), typeof(PairingMessage.FromUser), typeof(PairingMessage.Narration)],
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

        var told = Assert.Single(_driver.Told).Text;
        Assert.Contains("Should this retry too?", told);
        Assert.Contains("void Fetch()", told);
        Assert.Contains("`src/Client.cs`, lines 10-12", told);
    }

    [Fact]
    public void ABlankConversation_TellsTheAgentAboutDiffDino_WithTheUsersFirstWordsOnly()
    {
        var conversation = Create();
        conversation.OpenOnFirstWords();
        conversation.MarkRunning();

        conversation.Say("what does this repo do?");
        conversation.Say("and the tests?");

        Assert.Equal(2, _driver.Told.Count);
        Assert.StartsWith(PairingInstructions.ChatOpening(_repo.Path), _driver.Told[0].Text);
        Assert.EndsWith("what does this repo do?", _driver.Told[0].Text);
        Assert.Equal(new AgentPrompt("and the tests?"), _driver.Told[1]);
    }

    [Fact]
    public void ASessionStartedInABlankConversation_GetsTheWholeOpening()
    {
        var conversation = Create();
        conversation.OpenOnFirstWords();
        conversation.MarkRunning();

        conversation.BeginSession("Add a retry");

        Assert.Equal([new AgentPrompt(PairingInstructions.Opening("Add a retry", _repo.Path))], _driver.Told);
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
