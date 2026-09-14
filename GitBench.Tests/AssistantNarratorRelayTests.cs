using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using GitBench.Messages;
using Xunit;
using static GitBench.Tests.WalkthroughSteps;

namespace GitBench.Tests;

/// <summary>
/// The window's line to the assistant: the header's request and the store's cues go out as one
/// message for the window's repository, and an MCP narrator's moves never do.
/// </summary>
public sealed class AssistantNarratorRelayTests : IDisposable
{
    private readonly RecordingPresentation _presentation = new();
    private readonly ReviewWalkthroughStore _store;
    private readonly MessageBus _bus = new();
    private readonly Guid _repoId = Guid.NewGuid();
    private readonly List<NarrateWalkthroughMessage> _sent = new();
    private readonly AssistantNarratorRelay _relay;

    public AssistantNarratorRelayTests()
    {
        _store = new ReviewWalkthroughStore(_presentation, new QueuedDispatcher(), new ManualTimeProvider());
        _bus.Subscribe<NarrateWalkthroughMessage>(_sent.Add);
        _relay = new AssistantNarratorRelay(_store, _repoId, _bus);
    }

    public void Dispose()
    {
        _relay.Dispose();
        _store.Dispose();
    }

    [Fact]
    public void Begin_AsksForAWalkthroughOfTheWindowsRepository()
    {
        _relay.Begin();

        var message = Assert.Single(_sent);
        Assert.Equal(_repoId, message.RepoId);
        Assert.IsType<WalkthroughCue.Begin>(message.Cue);
    }

    [Fact]
    public void AnAssistantNarratedMove_GoesOutWithTheStepAndTheSelection()
    {
        _store.Show(AssistantNarrator, [Step("one"), Step("two")]);
        _store.Next();
        var selection = new DiffSelectionQuote("a.txt", new FileLine(3), null, DiffQuoteSide.Added, "x");
        _presentation.SelectionState.Value = selection;

        _store.Next();
        _store.Ask("why?");

        Assert.Equal(2, _sent.Count);
        Assert.All(_sent, m => Assert.Equal(_repoId, m.RepoId));
        Assert.Equal(1, Assert.IsType<WalkthroughCue.Next>(_sent[0].Cue).At);
        var ask = Assert.IsType<WalkthroughCue.Ask>(_sent[1].Cue);
        Assert.Equal("why?", ask.Question);
        Assert.Same(selection, ask.Selection);
    }

    [Fact]
    public void AnMcpNarratedMove_StaysWithItsWait()
    {
        _store.Show(Agent, [Step("one")]);

        _store.Next();
        _store.Ask("why?");

        Assert.Empty(_sent);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        _store.Show(AssistantNarrator, [Step("one")]);
        _relay.Dispose();

        _store.Next();

        Assert.Empty(_sent);
    }
}
