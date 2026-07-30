using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// A turn can finish having emitted nothing at all — a reasoning model that spends its whole budget
// thinking, an endpoint that stops with an empty choice. Left alone that reads as the app having
// swallowed the question, so the transcript says what happened and keeps what was asked.
public sealed class AssistantEmptyTurnTests : IDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));

    private Strings Text => _loc.Strings.Value;

    [Fact]
    public void ATurnThatSaysNothingSaysSoInTheTranscript()
    {
        using var session = Session(new FakeAssistantBackend(Silence(StopReason.EndTurn)));

        Ask(session, "which branch am I on?");

        var notice = Assert.Single(session.Rows, r => r.Kind == AssistantRowKind.Notice);
        Assert.Equal(Text.AssistantEmptyReply, notice.Text.Value);
        Assert.DoesNotContain(session.Rows, r => r.Kind == AssistantRowKind.Reply);
    }

    // The whole point of the notice: the reader can still see what they asked next to the fact that
    // nothing came back.
    [Fact]
    public void TheQuestionStaysInTheTranscript()
    {
        using var session = Session(new FakeAssistantBackend(Silence(StopReason.EndTurn)));

        Ask(session, "which branch am I on?");

        var asked = Assert.Single(session.Rows, r => r.Kind == AssistantRowKind.User);
        Assert.Equal("which branch am I on?", asked.Text.Value);
    }

    // And the model is not left reasoning from a history the person can see but it cannot.
    [Fact]
    public void TheQuestionIsStillReplayedToTheModel()
    {
        var backend = new FakeAssistantBackend(Silence(StopReason.EndTurn), Answer("on main"));
        using var session = Session(backend);

        Ask(session, "which branch am I on?");
        Ask(session, "and now?");

        Assert.Equal(
            ["which branch am I on?", "and now?"],
            backend.Requests[^1].Messages.OfType<AssistantMessage.User>().Select(m => m.Text));
    }

    // Running out of room is a different situation with a different way out, so it does not get the
    // same sentence as a model that simply had nothing to say.
    [Fact]
    public void RunningOutOfRoomReadsDifferentlyFromSayingNothing()
    {
        using var length = Session(new FakeAssistantBackend(Silence(StopReason.MaxTokens)));
        using var empty = Session(new FakeAssistantBackend(Silence(StopReason.EndTurn)));

        Ask(length, "summarise every commit");
        Ask(empty, "summarise every commit");

        var hitTheLimit = Assert.Single(length.Rows, r => r.Kind == AssistantRowKind.Notice);
        var saidNothing = Assert.Single(empty.Rows, r => r.Kind == AssistantRowKind.Notice);
        Assert.Equal(Text.AssistantEmptyReplyLength, hitTheLimit.Text.Value);
        Assert.NotEqual(saidNothing.Text.Value, hitTheLimit.Text.Value);
    }

    [Fact]
    public void AnAnsweredTurnIsOneReplyAndNoNotice()
    {
        using var session = Session(new FakeAssistantBackend(Answer("on main")));

        Ask(session, "which branch am I on?");

        var reply = Assert.Single(session.Rows, r => r.Kind == AssistantRowKind.Reply);
        Assert.Equal("on main", reply.Text.Value);
        Assert.DoesNotContain(session.Rows, r => r.Kind == AssistantRowKind.Notice);
    }

    // A turn whose work was tool calls has already shown the reader what it did, so a round that
    // adds no text after them is not silence.
    [Fact]
    public void ATurnThatRanToolsIsLeftAlone()
    {
        var backend = new FakeAssistantBackend(
            [
                new BackendEvent.ToolUse("call_1", "alpha", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            ],
            Silence(StopReason.EndTurn));
        using var session = Session(backend);

        Ask(session, "what changed?");

        Assert.Contains(session.Rows, r => r.Kind == AssistantRowKind.Tool);
        Assert.DoesNotContain(session.Rows, r => r.Kind == AssistantRowKind.Notice);
    }

    // The advisory for a provider whose tool calling was never demonstrated is itself an account of
    // the turn, so it stands alone rather than being doubled up with the empty-turn one.
    [Fact]
    public void AProviderWithNoProvenToolCallingStillGetsItsOwnNotice()
    {
        using var session = Session(
            new FakeAssistantBackend(Silence(StopReason.EndTurn)), toolCallingIsUnproven: true);

        Ask(session, "which branch am I on?");

        var notice = Assert.Single(session.Rows, r => r.Kind == AssistantRowKind.Notice);
        Assert.Equal(Text.AssistantNoToolSupport, notice.Text.Value);
    }

    private static BackendEvent[] Silence(StopReason reason) => [new BackendEvent.TurnComplete(reason)];

    private static BackendEvent[] Answer(string text) =>
        [new BackendEvent.TextDelta(text), new BackendEvent.TurnComplete(StopReason.EndTurn)];

    private AssistantSession Session(FakeAssistantBackend backend, bool toolCallingIsUnproven = false)
    {
        var agent = new AgentDefinition("test", "You are a test agent.", ["alpha"], ModelTier.Chat);
        var loop = new AssistantAgentLoop(
            backend,
            agent,
            AssistantToolset.Create([new StubTool("alpha")], ["alpha"]),
            () => toolCallingIsUnproven);
        var repo = new Repo(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "repo"), "repo");
        return new AssistantSession(repo, new GitService(new NullActivityTracker()), loop, _loc, _dispatcher);
    }

    private void Ask(AssistantSession session, string message)
    {
        session.Send(message);
        Pump.WaitFor(_dispatcher, () => !session.IsBusy.Value, "the turn to finish");
    }

    public void Dispose() => _loc.Dispose();
}
