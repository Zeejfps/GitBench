using System.Diagnostics;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;
using static GitBench.Tests.WalkthroughSteps;

namespace GitBench.Tests;

/// <summary>
/// The built-in assistant as narrator, end to end from the window's message: the walkthrough agent
/// runs in the window repository's own conversation with the presentation tools in hand, keeps its
/// memory across the reviewer's moves, holds a move that lands mid-turn, writes its prose under the
/// step the reviewer is on, and — with nothing to answer with — tells the rail so.
/// </summary>
public sealed class AssistantWalkthroughNarrationTests : IDisposable
{
    private const string OneStep =
        """
        {"steps":[{"title":"The entry point","body_md":"It starts **here**."}]}
        """;

    private readonly ReviewPresentationFixture _fixture = new();
    private readonly TempDir _otherDir = new("gitbench-narration-other-");
    private AssistantSessionStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        _otherDir.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void Begin_RunsTheWalkthroughAgent_InTheWindowRepositorysConversation_WithThePresentationTools()
    {
        var backend = Answering("Looking.");
        var store = Start(backend, key: "sk-test");
        var window = _fixture.OpenWindow();
        var other = OpenOtherRepo();
        _fixture.Registry.SetActive(other.Id);

        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();

        var request = Assert.Single(backend.Requests);
        var expected = AgentCatalog.LoadEmbedded().Get(AgentCatalog.WalkthroughReviewAgent);
        Assert.Equal(expected.SystemPrompt, request.SystemPrompt);
        var offered = Assert.Single(backend.OfferedTools);
        Assert.Contains("walkthrough_step", offered);
        Assert.Contains("walkthrough_end", offered);
        Assert.Contains("review_focus", offered);
        Assert.Contains("review_state", offered);
        Assert.Contains("get_review_diff", offered);
        Assert.DoesNotContain(offered, name => name == "mark_viewed");

        // The other repository's conversation, the active one, saw nothing of it.
        Assert.Empty(store.Active.Value!.Rows);
        _fixture.Registry.SetActive(_fixture.Repo.Id);
        var rows = store.Active.Value!.Rows;
        Assert.Equal(AssistantRowKind.User, rows[0].Kind);
        Assert.Equal("Walk me through this change.", rows[0].Text.Value);
        Assert.Equal(window.Session.RepoId, store.Active.Value!.RepoId);
    }

    [Fact]
    public void ProseOutsideTheStepCall_LandsUnderTheStepTheReviewerIsOn_AndTheNarratorThenWaits()
    {
        var backend = new FakeAssistantBackend(
            [
                new BackendEvent.ToolUse("call_1", "walkthrough_step", AssistantTestJson.Element(OneStep)),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            ],
            [
                new BackendEvent.TextDelta("Note "),
                new BackendEvent.TextDelta("this."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            ]);
        Start(backend, key: "sk-test");
        var window = _fixture.OpenWindow();

        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();

        var walkthrough = window.Walkthrough;
        var card = Assert.IsType<WalkthroughCard.Step>(walkthrough.Card.Value);
        Assert.Equal("The entry point", card.Content.Title);
        Assert.Equal("Note this.", WalkthroughExchanges.Narration(walkthrough));
        var phase = Assert.IsType<WalkthroughPhase.Narrating>(walkthrough.Phase.Value);
        Assert.Equal(NarratorKind.Assistant, phase.Who.Kind);
        Assert.True(phase.Waiting);
    }

    // The rail is the assistant's only surface in the window, so a turn that dies before its first
    // step has to say so there: the failure lands on the preparing card and the narrator is marked
    // gone, rather than the card saying the assistant is working on something it never will send.
    [Fact]
    public void AFailureBeforeTheFirstStep_IsShownOnThePreparingCard()
    {
        var backend = new FakeAssistantBackend([new BackendEvent.Error("API key is invalid.")]);
        Start(backend, key: "sk-test");
        var window = _fixture.OpenWindow();

        window.StartWalkthrough();
        Assert.IsType<WalkthroughCard.Preparing>(window.Walkthrough.Card.Value);
        WaitForIdle();

        var walkthrough = window.Walkthrough;
        Assert.IsType<WalkthroughCard.Preparing>(walkthrough.Card.Value);
        Assert.IsType<WalkthroughPhase.Disconnected>(walkthrough.Phase.Value);
        Assert.Equal(["Failed: API key is invalid."], WalkthroughExchanges.Lines(walkthrough));
        Assert.False(walkthrough.IsStarting.Value);
    }

    [Fact]
    public void AFrontierNext_ContinuesTheSameThread()
    {
        var backend = new FakeAssistantBackend(
            [
                new BackendEvent.ToolUse("call_1", "walkthrough_step", AssistantTestJson.Element(OneStep)),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            ],
            [new BackendEvent.TurnComplete(StopReason.EndTurn)],
            [new BackendEvent.TextDelta("More."), new BackendEvent.TurnComplete(StopReason.EndTurn)]);
        Start(backend, key: "sk-test");
        var window = _fixture.OpenWindow();
        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();

        window.Walkthrough.Next();
        WaitForIdle();

        Assert.Equal(3, backend.Requests.Count);
        var users = backend.Requests[2].Messages.OfType<AssistantMessage.User>().Select(u => u.Text).ToList();
        Assert.Equal("Walk me through this change.", users[0]);
        Assert.StartsWith("The reviewer stepped past step 1.", users[1], StringComparison.Ordinal);
        Assert.Contains(backend.Requests[2].Messages, m => m is AssistantMessage.ToolResults);
        Assert.Equal("More.", WalkthroughExchanges.Narration(window.Walkthrough));
    }

    [Fact]
    public void AQuestion_CarriesTheStepAndTheSelectionQuote()
    {
        var backend = Answering("Because.", "That is the guard.");
        Start(backend, key: "sk-test");
        var window = _fixture.OpenWindow();
        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();

        Narrate(new WalkthroughCue.Ask(
            0, "why?", new DiffSelectionQuote("a.txt", new FileLine(5), null, DiffQuoteSide.Added, "a line 5 changed")));
        WaitForIdle();

        var ask = backend.Requests[1].Messages.OfType<AssistantMessage.User>().Last().Text;
        Assert.StartsWith("At step 1 the reviewer asks: why?", ask, StringComparison.Ordinal);
        Assert.Contains("`a.txt`, line 5", ask, StringComparison.Ordinal);
        Assert.Contains("a line 5 changed", ask, StringComparison.Ordinal);
    }

    // The step tool hops to the UI thread and this test is the UI thread, so the first turn is
    // still running when the second cue lands: it is held rather than dropped, and sent once the
    // turn has ended.
    [Fact]
    public void AMoveDuringTheTurn_IsHeld_AndSentWhenTheTurnEnds()
    {
        var backend = new FakeAssistantBackend(
            [
                new BackendEvent.ToolUse("call_1", "walkthrough_step", AssistantTestJson.Element(OneStep)),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            ],
            [new BackendEvent.TurnComplete(StopReason.EndTurn)],
            [new BackendEvent.TurnComplete(StopReason.EndTurn)]);
        Start(backend, key: "sk-test");
        _fixture.OpenWindow();

        Narrate(new WalkthroughCue.Begin());
        Narrate(new WalkthroughCue.Next(0));
        Narrate(new WalkthroughCue.Next(1));
        WaitForIdle();

        Assert.Equal(3, backend.Requests.Count);
        var last = backend.Requests[2].Messages.OfType<AssistantMessage.User>().Last().Text;
        Assert.StartsWith("The reviewer stepped past step 2.", last, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginAgain_StartsTheThreadOver()
    {
        var backend = Answering("First.", "Second.");
        Start(backend, key: "sk-test");
        _fixture.OpenWindow();

        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();
        Narrate(new WalkthroughCue.Begin());
        WaitForIdle();

        Assert.Equal(2, backend.Requests.Count);
        Assert.Single(backend.Requests[1].Messages.OfType<AssistantMessage.User>());
    }

    // A key from the environment would configure the assistant behind the fake secret store's
    // back, so the process's own variables are put aside while this runs.
    [Fact]
    public void WithNothingToAnswerWith_TheRailIsToldItsNarratorIsGone()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in AssistantProviders.All.Select(p => p.Hosting).OfType<AssistantHosting.Hosted>().Select(h => h.EnvironmentVariable))
        {
            environment[variable] = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, null);
        }

        try
        {
            var backend = Answering("Never.");
            var store = Start(backend, key: null);
            Assert.False(store.IsConfigured(AssistantRole.Walkthrough).Value);
            var window = _fixture.OpenWindow();
            window.Walkthrough.Show(AssistantNarrator, [Step("one")]);

            window.Walkthrough.Next();
            Pump.DrainFor(_fixture.Dispatcher, TimeSpan.FromMilliseconds(200));

            Assert.IsType<WalkthroughPhase.Disconnected>(window.Walkthrough.Phase.Value);
            Assert.Empty(backend.Requests);
        }
        finally
        {
            foreach (var (variable, value) in environment)
                Environment.SetEnvironmentVariable(variable, value);
        }
    }

    private AssistantSessionStore Start(FakeAssistantBackend backend, string? key)
    {
        var store = new AssistantSessionStore(
            _fixture.Registry,
            _fixture.Git,
            new UnparsedFiles(),
            new AssistantCredentials(new FakeSecretStore(key)),
            new State<AssistantSettings>(AssistantSettings.Default),
            _fixture.Localization,
            _fixture.Dispatcher,
            _fixture.Bus,
            new SilentCommitEditor(),
            new ReviewProgressStore(),
            _fixture.Windows,
            new IdleRemoteOperations(),
            new TestDocuments.Empty(),
            (_, _) => backend);
        _store = store;
        store.Start();
        if (key is not null)
            Pump.WaitFor(_fixture.Dispatcher, () => store.IsConfigured(AssistantRole.Walkthrough).Value, "the API key to resolve");
        else
            Pump.DrainFor(_fixture.Dispatcher, TimeSpan.FromMilliseconds(100));
        return store;
    }

    private void Narrate(WalkthroughCue cue) =>
        _fixture.Bus.Broadcast(new NarrateWalkthroughMessage(_fixture.Repo.Id, cue));

    // Every conversation this test can reach is the window repository's; the active one is read
    // through the store, so the fixture's repository is made active for the check.
    private void WaitForIdle()
    {
        var active = _fixture.Registry.Active.Value;
        _fixture.Registry.SetActive(_fixture.Repo.Id);
        var session = _store!.Active.Value!;
        Pump.WaitFor(_fixture.Dispatcher, () => !session.IsBusy.Value, "the walkthrough turn to finish");
        if (active is { } previous) _fixture.Registry.SetActive(previous.Id);
    }

    private Repo OpenOtherRepo()
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _otherDir.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("-q");
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (_fixture.Registry.Open(_otherDir.Path) != OpenRepoOutcome.Opened)
            throw new InvalidOperationException("The second repository did not open.");
        return _fixture.Registry.Repos.Single(r => r.Id != _fixture.Repo.Id);
    }

    private static FakeAssistantBackend Answering(params string[] answers) =>
        new(answers.Select(text => new BackendEvent[]
        {
            new BackendEvent.TextDelta(text),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        }).ToArray());
}
