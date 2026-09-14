using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using Xunit;
using static GitBench.Tests.WalkthroughSteps;

namespace GitBench.Tests;

/// <summary>
/// The walkthrough's hand-off between narrator and reviewer: a batch walks locally and returns
/// control only past its frontier, one waiter at a time, a click with nobody waiting is latched,
/// and the bounded wait, a new batch, an end or a disconnect all answer the waiter.
/// </summary>
public sealed class ReviewWalkthroughStoreTests
{
    private readonly RecordingPresentation _presentation = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly ReviewWalkthroughStore _store;

    public ReviewWalkthroughStoreTests()
    {
        _store = new ReviewWalkthroughStore(_presentation, _dispatcher, _clock);
    }

    [Fact]
    public void Idle_ShowsNothing()
    {
        Assert.IsType<WalkthroughPhase.Idle>(_store.Phase.Value);
        Assert.IsType<WalkthroughCard.None>(_store.Card.Value);
        Assert.False(_store.IsVisible.Value);
        Assert.Equal(-1, _store.Current.Value);
    }

    [Fact]
    public void Show_PutsTheFirstStepUp_AndAppliesItsPresentation()
    {
        _store.Show(Agent, [Step("one", "a.txt", 12, Spot("a.txt", 10, 14)), Step("two")]);

        var card = Assert.IsType<WalkthroughCard.Step>(_store.Card.Value);
        Assert.Equal(0, card.Index);
        Assert.Equal(2, card.Count);
        Assert.Equal("one", card.Content.Title);
        Assert.Equal(1, _store.Frontier.Value);
        var phase = Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value);
        Assert.Equal(Agent, phase.Who);
        Assert.False(phase.Waiting);
        Assert.Equal(["focus a.txt:12:New", "spotlights 1 dim=False"], _presentation.Calls);
    }

    [Fact]
    public void Next_WalksTheBatchLocally_AndOnlyPastTheFrontierReachesTheWaiter()
    {
        _store.Show(Agent, [Step("one"), Step("two"), Step("three")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Next();
        Assert.Equal(1, _store.Current.Value);
        Assert.False(wait.IsCompleted);
        _store.Next();
        Assert.Equal(2, _store.Current.Value);
        Assert.False(wait.IsCompleted);

        _store.Next();

        var next = Assert.IsType<WalkthroughAction.Next>(Result(wait));
        Assert.Equal(2, next.At);
        Assert.Equal(2, _store.Current.Value);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
    }

    [Fact]
    public void Back_ReShowsThePreviousStep_WithoutTouchingTheWaiter()
    {
        _store.Show(Agent, [Step("one", "a.txt", 1), Step("two", "b.txt", 2)]);
        var wait = _store.WaitAsync(CancellationToken.None);
        _store.Next();
        _presentation.Calls.Clear();

        _store.Back();

        Assert.Equal(0, _store.Current.Value);
        Assert.Equal(["focus a.txt:1:New", "clear"], _presentation.Calls);
        Assert.False(wait.IsCompleted);
        Assert.False(_store.CanGoBack.Value);

        _store.Back();
        Assert.Equal(0, _store.Current.Value);
    }

    [Fact]
    public void Ask_CarriesTheQuestionAndTheSelection()
    {
        _store.Show(Agent, [Step("one")]);
        var quote = new DiffSelectionQuote("a.txt", new FileLine(3), new FileLine(5), DiffQuoteSide.Added, "x = 1");
        _presentation.SelectionState.Value = quote;
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Ask("  why?  ");

        var ask = Assert.IsType<WalkthroughAction.Ask>(Result(wait));
        Assert.Equal("why?", ask.Question);
        Assert.Same(quote, ask.Selection);
        Assert.Equal(0, ask.At);
    }

    [Fact]
    public void ABlankAsk_IsIgnored()
    {
        _store.Show(Agent, [Step("one")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Ask("   ");

        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public void TheBoundedWait_AnswersPending()
    {
        _store.Show(Agent, [Step("one")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _clock.Advance(ReviewWalkthroughStore.WaitTimeout - TimeSpan.FromSeconds(1));
        _dispatcher.Drain();
        Assert.False(wait.IsCompleted);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _dispatcher.Drain();

        Assert.IsType<WalkthroughAction.Pending>(Result(wait));
        Assert.Equal(0, _clock.LiveTimers);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
    }

    [Fact]
    public void ASecondWait_CancelsTheFirst()
    {
        _store.Show(Agent, [Step("one")]);
        var first = _store.WaitAsync(CancellationToken.None);

        var second = _store.WaitAsync(CancellationToken.None);

        Assert.IsType<WalkthroughAction.Cancelled>(Result(first));
        Assert.False(second.IsCompleted);
        _store.Next();
        Assert.IsType<WalkthroughAction.Next>(Result(second));
    }

    [Fact]
    public void AnActionWithNoWaiter_IsLatched_AndHandedToTheNextWaitAtOnce()
    {
        _store.Show(Agent, [Step("one")]);

        _store.Next();
        var wait = _store.WaitAsync(CancellationToken.None);

        Assert.True(wait.IsCompleted);
        Assert.IsType<WalkthroughAction.Next>(Result(wait));
        Assert.False(_store.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void ALaterAction_ReplacesTheLatchedOne()
    {
        _store.Show(Agent, [Step("one")]);

        _store.Next();
        _store.Ask("what?");
        var wait = _store.WaitAsync(CancellationToken.None);

        Assert.IsType<WalkthroughAction.Ask>(Result(wait));
    }

    [Fact]
    public void ShowWhileWaiting_CancelsTheWaiter_AndDropsTheLatch()
    {
        _store.Show(Agent, [Step("one")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Show(Agent, [Step("two"), Step("three")]);

        var cancelled = Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        Assert.Equal(0, cancelled.At);
        Assert.Equal(1, _store.Current.Value);
        Assert.Equal(2, _store.Frontier.Value);
        Assert.Equal(3, _store.Steps.Value.Count);

        _store.Next();
        _store.Show(Agent, [Step("four")]);
        Assert.False(_store.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void End_CancelsTheWaiter_ClearsSpotlights_AndLeavesTheSummaryUp()
    {
        _store.Show(Agent, [Step("one", "a.txt", 1, Spot("a.txt", 1, 2))]);
        var wait = _store.WaitAsync(CancellationToken.None);
        _presentation.Calls.Clear();

        _store.End("**done**");

        Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        Assert.Contains("clear", _presentation.Calls);
        Assert.Empty(_store.Steps.Value);
        Assert.IsType<WalkthroughPhase.Idle>(_store.Phase.Value);
        var finished = Assert.IsType<WalkthroughCard.Finished>(_store.Card.Value);
        Assert.Equal("**done**", finished.SummaryMarkdown);
        Assert.True(_store.IsVisible.Value);

        _store.Clear();
        Assert.False(_store.IsVisible.Value);
    }

    [Fact]
    public void EndWithoutASummary_TakesTheRailDown()
    {
        _store.Show(Agent, [Step("one")]);

        _store.End(null);

        Assert.False(_store.IsVisible.Value);
        Assert.IsType<WalkthroughCard.None>(_store.Card.Value);
    }

    [Fact]
    public void MarkDisconnected_CancelsTheWaiter_AndDisablesNext()
    {
        _store.Show(Agent, [Step("one")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.MarkDisconnected();

        Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        var phase = Assert.IsType<WalkthroughPhase.Disconnected>(_store.Phase.Value);
        Assert.Equal(Agent, phase.Who);
        Assert.True(_store.IsVisible.Value);
        Assert.False(_store.CanGoNext.Value);

        _store.Next();
        Assert.False(_store.WaitAsync(CancellationToken.None).IsCompleted);

        _store.Clear();
        Assert.IsType<WalkthroughPhase.Idle>(_store.Phase.Value);
        Assert.False(_store.IsVisible.Value);
    }

    [Fact]
    public void TheCallersCancellation_AnswersCancelled_AndDetaches()
    {
        _store.Show(Agent, [Step("one")]);
        using var cts = new CancellationTokenSource();
        var wait = _store.WaitAsync(cts.Token);

        cts.Cancel();
        _dispatcher.Drain();

        Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        Assert.Equal(0, _clock.LiveTimers);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
        // Nothing is attached any more, so the next click latches instead of resolving a ghost.
        _store.Next();
        Assert.IsType<WalkthroughAction.Next>(Result(_store.WaitAsync(CancellationToken.None)));
    }

    [Fact]
    public void WaitingIsReflectedInThePhase()
    {
        _store.Show(Agent, [Step("one")]);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);

        var wait = _store.WaitAsync(CancellationToken.None);
        Assert.True(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);

        _store.Next();
        Result(wait);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
    }

    [Fact]
    public void AFailedFocus_IsKeptForTheRail_NotThrown()
    {
        _presentation.FocusAnswer = line => new ReviewLineResolution.NotInDiff(line, new FileLine(10), new FileLine(20));

        _store.Show(Agent, [Step("one", "a.txt", 15), Step("two")]);

        Assert.IsType<ReviewLineResolution.NotInDiff>(_store.LastFailure.Value);
        _store.Next();
        Assert.Null(_store.LastFailure.Value);
    }

    [Fact]
    public void FocusSpotlight_ReFocusesThatRange()
    {
        _store.Show(Agent, [Step("one", null, 1, Spot("a.txt", 3, 4), Spot("b.txt", 7, 9))]);
        _presentation.Calls.Clear();

        _store.FocusSpotlight(1);

        Assert.Equal(["focus b.txt:7:New"], _presentation.Calls);
    }

    [Fact]
    public void Begin_PutsThePreparingCardUp_UntilTheFirstStep()
    {
        _store.Begin(AssistantNarrator);

        Assert.IsType<WalkthroughCard.Preparing>(_store.Card.Value);
        Assert.True(_store.IsVisible.Value);
        Assert.True(_store.IsStarting.Value);
        var phase = Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value);
        Assert.Equal(AssistantNarrator, phase.Who);
        Assert.False(phase.Waiting);
        Assert.False(_store.CanGoNext.Value);
        Assert.False(_store.CanGoBack.Value);

        _store.Show(AssistantNarrator, [Step("one")]);

        Assert.IsType<WalkthroughCard.Step>(_store.Card.Value);
        Assert.False(_store.IsStarting.Value);
    }

    [Fact]
    public void Begin_SupersedesTheWalkthroughInProgress()
    {
        _store.Show(Agent, [Step("one"), Step("two")]);
        _store.Next();
        _store.AppendNarration("about two");
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Begin(AssistantNarrator);

        Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        Assert.Empty(_store.Steps.Value);
        Assert.Equal(-1, _store.Current.Value);
        Assert.Empty(_store.Exchange.Value);
        Assert.Equal("clear", _presentation.Calls.Last());
    }

    [Fact]
    public void ProseBeforeTheFirstStep_LandsOnThePreparingCard_AndStaysBehindOnceAStepIsUp()
    {
        _store.Begin(AssistantNarrator);
        Assert.True(_store.IsComposing.Value);

        _store.AppendNarration("\n\nLooking ");
        _store.AppendNarration("at the change.");
        Assert.Equal(["Looking at the change."], WalkthroughExchanges.Lines(_store));
        Assert.False(_store.IsComposing.Value);

        _store.Show(AssistantNarrator, [Step("one")]);
        Assert.Empty(_store.Exchange.Value);
        Assert.True(_store.IsComposing.Value);
    }

    [Fact]
    public void ProseWithNothingUp_IsDropped()
    {
        _store.AppendNarration("into the void");
        Assert.Empty(_store.Exchange.Value);
        Assert.IsType<WalkthroughCard.None>(_store.Card.Value);
    }

    [Fact]
    public void BlankFragments_OpenNoMessage()
    {
        _store.Show(Agent, [Step("one")]);

        _store.AppendNarration("\n\n");
        _store.AppendNarration("  ");

        Assert.Empty(_store.Exchange.Value);
    }

    // The assistant answered without ever showing a step — an error, a refusal, or plain prose. What
    // it said is what the reviewer needs to read, so it stays up, and nothing will follow it, so the
    // rail offers Clear rather than Next.
    [Fact]
    public void ATurnThatEndsBeforeTheFirstStep_KeepsWhatItSaid_UnderAClear()
    {
        _store.Begin(AssistantNarrator);
        _store.ReportFailure("API key is invalid.");

        _store.MarkNarratorWaiting();

        Assert.IsType<WalkthroughCard.Preparing>(_store.Card.Value);
        Assert.IsType<WalkthroughPhase.Disconnected>(_store.Phase.Value);
        Assert.Equal(["Failed: API key is invalid."], WalkthroughExchanges.Lines(_store));
        Assert.True(_store.IsVisible.Value);
        Assert.False(_store.IsStarting.Value);
        Assert.False(_store.IsComposing.Value);

        _store.Clear();
        Assert.IsType<WalkthroughCard.None>(_store.Card.Value);
    }

    [Fact]
    public void ASilentTurnBeforeTheFirstStep_TakesTheRailDown()
    {
        _store.Begin(AssistantNarrator);

        _store.MarkNarratorWaiting();

        Assert.IsType<WalkthroughCard.None>(_store.Card.Value);
        Assert.IsType<WalkthroughPhase.Idle>(_store.Phase.Value);
        Assert.False(_store.IsStarting.Value);
    }

    [Fact]
    public void TheExchange_FollowsTheStep()
    {
        _store.Show(Agent, [Step("one"), Step("two")]);
        _store.AppendNarration("about one");
        Assert.Equal(["about one"], WalkthroughExchanges.Lines(_store));

        _store.Next();
        Assert.Empty(_store.Exchange.Value);

        _store.Back();
        Assert.Equal(["about one"], WalkthroughExchanges.Lines(_store));
    }

    // Fragments continue the message the turn is writing on the step; a fragment for another step,
    // or one written after coming back, is a message of its own.
    [Fact]
    public void Narration_StreamsOntoOneMessagePerStepAndTurn()
    {
        _store.Show(Agent, [Step("one"), Step("two")]);

        _store.AppendNarration("\n\nabout ");
        _store.AppendNarration("one");
        Assert.Equal(["about one"], WalkthroughExchanges.Lines(_store));

        _store.Next();
        _store.AppendNarration("about two");
        _store.Back();
        _store.AppendNarration("\n\nmore");

        Assert.Equal(["about one", "more"], WalkthroughExchanges.Lines(_store));
        _store.Next();
        Assert.Equal(["about two"], WalkthroughExchanges.Lines(_store));
    }

    // The conversation reads as the chat does: the question, then the answer under it — never the
    // answer appended to what the narrator said before the question was asked.
    [Fact]
    public void AQuestion_JoinsTheExchange_AndTheAnswerStartsAfterIt()
    {
        _store.Show(AssistantNarrator, [Step("one")]);
        var quote = new DiffSelectionQuote("a.txt", new FileLine(3), new FileLine(5), DiffQuoteSide.Added, "x = 1");
        _presentation.SelectionState.Value = quote;
        _store.AppendNarration("Note this.");

        _store.Ask("why here?");
        Assert.True(_store.IsComposing.Value);
        _store.AppendNarration("Because.");

        Assert.Equal(["Note this.", "You: why here?", "Because."], WalkthroughExchanges.Lines(_store));
        var question = Assert.IsType<WalkthroughMessage.Question>(_store.Exchange.Value[1]);
        Assert.Same(quote, question.Selection);
        Assert.False(_store.IsComposing.Value);
    }

    // The turn ended and the narrator waits; what it says on its next turn is a new message, and
    // while that turn has said nothing yet the rail shows it thinking.
    [Fact]
    public void ANewTurn_StartsANewMessage()
    {
        _store.Show(AssistantNarrator, [Step("one")]);
        _store.AppendNarration("First turn.");
        _store.MarkNarratorWaiting();
        Assert.False(_store.IsComposing.Value);

        _store.Show(AssistantNarrator, [Step("two")]);
        _store.Back();
        _store.AppendNarration("Second turn.");

        Assert.Equal(["First turn.", "Second turn."], WalkthroughExchanges.Lines(_store));
    }

    [Fact]
    public void ARefusal_IsANoticeOnTheExchange()
    {
        _store.Show(AssistantNarrator, [Step("one")]);

        _store.ReportRefusal("Not about this change.");

        Assert.Equal(["Refused: Not about this change."], WalkthroughExchanges.Lines(_store));
    }

    // The assistant never attaches a wait, so its channel is the cue: a frontier Next reaches it
    // at once and is not held for a wait that would never come, and the rail reads it as composing
    // until whoever runs the assistant says its turn ended.
    [Fact]
    public void AnAssistantNarratorsFrontierNext_IsRaisedAsACue_AndNeverLatched()
    {
        var cues = new List<WalkthroughCue.Move>();
        _store.AssistantCued += cues.Add;
        _store.Show(AssistantNarrator, [Step("one"), Step("two")]);

        _store.Next();
        Assert.Empty(cues);

        _store.Next();

        var next = Assert.IsType<WalkthroughCue.Next>(Assert.Single(cues));
        Assert.Equal(1, next.At);
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
        Assert.False(_store.WaitAsync(CancellationToken.None).IsCompleted);

        _store.MarkNarratorWaiting();
        Assert.True(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);
    }

    [Fact]
    public void AnAssistantNarratorsAsk_CarriesTheSelection()
    {
        var cues = new List<WalkthroughCue.Move>();
        _store.AssistantCued += cues.Add;
        _store.Show(AssistantNarrator, [Step("one")]);
        var selection = new DiffSelectionQuote("a.txt", new FileLine(3), new FileLine(4), DiffQuoteSide.Added, "x");
        _presentation.SelectionState.Value = selection;

        _store.Ask("  why?  ");

        var ask = Assert.IsType<WalkthroughCue.Ask>(Assert.Single(cues));
        Assert.Equal(0, ask.At);
        Assert.Equal("why?", ask.Question);
        Assert.Same(selection, ask.Selection);
    }

    // The MCP session's wait is the only one MarkNarratorWaiting must never touch: its phase is
    // the wait's own.
    [Fact]
    public void AnMcpNarratorsMoves_StillAnswerTheWait_AndIgnoreTheAssistantsSignals()
    {
        var cues = new List<WalkthroughCue.Move>();
        _store.AssistantCued += cues.Add;
        _store.Show(Agent, [Step("one")]);

        _store.MarkNarratorWaiting();
        Assert.False(Assert.IsType<WalkthroughPhase.Narrating>(_store.Phase.Value).Waiting);

        _store.Next();

        Assert.Empty(cues);
        Assert.IsType<WalkthroughAction.Next>(Result(_store.WaitAsync(CancellationToken.None)));
    }

    [Fact]
    public void Dispose_CancelsTheWaiter_AndRefusesFurtherUse()
    {
        _store.Show(Agent, [Step("one")]);
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Dispose();

        Assert.IsType<WalkthroughAction.Cancelled>(Result(wait));
        Assert.Throws<ObjectDisposedException>(() => _store.Show(Agent, [Step("two")]));
    }

    private static WalkthroughAction Result(Task<WalkthroughAction> wait)
    {
        Assert.True(wait.Wait(TimeSpan.FromSeconds(5)), "the wait did not complete");
        return wait.Result;
    }
}
