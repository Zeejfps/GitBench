using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>The pairing loop's hand-off: one open stop at a time, Done returning the user's diff
/// since the stop was shown, moves queued while the agent isn't waiting, and the wait bounded the
/// way the walkthrough's is.</summary>
public sealed class PairingStoreTests : IDisposable
{
    private readonly RecordingPairingPresentation _presentation = new();
    private readonly ScriptedWorkspace _workspace = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly PairingStore _store;

    public PairingStoreTests()
    {
        _store = new PairingStore("Add a retry", "Claude Code", _presentation, _workspace, _dispatcher, _clock);
        _store.MarkRunning();
    }

    public void Dispose() => _store.Dispose();

    private T Await<T>(Task<T> task, string what)
    {
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);
        return task.Result;
    }

    private OpenStop Open(string symbol = "Fetch", bool replace = false)
    {
        var opening = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", symbol, null), "Retry " + symbol, "Because.", PairingStopKind.Edit, replace, CancellationToken.None),
            "the stop to open");
        return Assert.IsType<StopOpening.Opened>(opening).Stop;
    }

    [Fact]
    public void OpenStop_PlacesItInTheEditor_AndNumbersIt()
    {
        var stop = Open();

        Assert.Equal(1, stop.Stop.Number);
        Assert.Equal(["show src/Client.cs#Fetch", "clear hints", "reveal"], _presentation.Calls);
        Assert.Same(stop, _store.Stop.Value);
    }

    [Fact]
    public void ASecondStop_IsRefusedWhileOneIsOpen()
    {
        Open();

        var second = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", "Send", null), "t", "r", PairingStopKind.Edit, false, CancellationToken.None),
            "the refusal");

        var refused = Assert.IsType<StopOpening.Refused>(second);
        Assert.Contains("still open", refused.Message);
        Assert.Equal(1, _store.Stop.Value!.Stop.Number);
    }

    [Fact]
    public void ASecondStop_ThatReplaces_TakesThePlaceOfTheFirst()
    {
        Open();

        var second = Open("Send", replace: true);

        Assert.Equal(2, second.Stop.Number);
        Assert.Same(second, _store.Stop.Value);
    }

    [Fact]
    public void AStopForAMissingSymbol_IsRefusedWithWhatIsDeclared()
    {
        _presentation.Answer = t => new StopPlacement.Missed(new StopMiss.NoSuchSymbol(t.Path, t.Symbol, ["Client", "Client.Send"]));

        var opening = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", "Nope", null), "t", "r", PairingStopKind.Edit, false, CancellationToken.None),
            "the refusal");

        var refused = Assert.IsType<StopOpening.Refused>(opening);
        Assert.Contains("Client.Send", refused.Message);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void Done_ReturnsTheDiffSinceTheStopWasShown_AndClosesTheStop()
    {
        _workspace.Current = "before";
        Open();
        _workspace.Current = "after";
        _workspace.Diffs[("before", "after")] = "+ retry";
        var wait = _store.WaitAsync(CancellationToken.None);

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted && wait.IsCompleted, "Done to reach the agent");

        var action = Assert.IsType<PairingAction.Done>(wait.Result);
        Assert.Equal(1, action.Stop);
        Assert.Equal("+ retry", action.Diff);
        Assert.Contains("save", _presentation.Calls);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void Done_WithNoWaitAttached_IsHeldForTheNextWait()
    {
        Open();
        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");

        var wait = _store.WaitAsync(CancellationToken.None);

        Assert.True(wait.IsCompleted);
        Assert.IsType<PairingAction.Done>(wait.Result);
    }

    [Fact]
    public void QueuedMoves_ComeOutInOrder_SoAMessageNeverHidesADone()
    {
        Open();
        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");
        _store.Say("Why a loop?");

        Assert.IsType<PairingAction.Done>(_store.WaitAsync(CancellationToken.None).Result);
        var ask = Assert.IsType<PairingAction.Message>(_store.WaitAsync(CancellationToken.None).Result);
        Assert.Equal("Why a loop?", ask.Text);
    }

    [Fact]
    public void Say_CarriesTheCaret()
    {
        Open();
        _presentation.CaretState.Value = new EditorCaret("C:/repo/src/Client.cs", TextPosition.At(12, 3), "retries");
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Say("Is this the right place?");

        var ask = Assert.IsType<PairingAction.Message>(wait.Result);
        Assert.Equal("retries", ask.Caret!.SelectedText);
        Assert.Contains(_store.Messages, m => m is PairingMessage.FromUser { Text: "Is this the right place?" });
    }

    [Fact]
    public void Wait_RunsOutAsPending()
    {
        var wait = _store.WaitAsync(CancellationToken.None);

        _clock.Advance(PairingStore.WaitTimeout);
        _dispatcher.Drain();

        Assert.IsType<PairingAction.Pending>(wait.Result);
    }

    [Fact]
    public void ANewerWait_CancelsTheOlder()
    {
        var first = _store.WaitAsync(CancellationToken.None);
        var second = _store.WaitAsync(CancellationToken.None);

        Assert.IsType<PairingAction.Cancelled>(first.Result);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public void Waiting_IsVisibleInThePhase()
    {
        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);
        var wait = _store.WaitAsync(CancellationToken.None);
        Assert.Equal(new PairingPhase.Running(true), _store.Phase.Value);

        _store.Say("?");

        Assert.True(wait.IsCompleted);
        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);
    }

    [Fact]
    public void EndByUser_AnswersTheWaitWithEnded()
    {
        Open();
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.EndByUser();

        Assert.IsType<PairingAction.Ended>(wait.Result);
        Assert.IsType<PairingPhase.Ended>(_store.Phase.Value);
        Assert.Null(_store.Stop.Value);
        Assert.IsType<PairingAction.Ended>(_store.WaitAsync(CancellationToken.None).Result);
    }

    [Fact]
    public void Skip_ClosesTheStopWithoutADiff()
    {
        Open();
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.Skip();

        Assert.IsType<PairingAction.Skipped>(wait.Result);
        Assert.Null(_store.Stop.Value);
        Assert.Equal(1, _workspace.Captures);
    }

    [Fact]
    public void Hints_GoUpOneLevelAtATime_AndResetAtTheNextStop()
    {
        Open();
        var wait = _store.WaitAsync(CancellationToken.None);

        _store.RequestHint();

        Assert.Equal(new PairingAction.Hint(1, HintLevel.Location), wait.Result);
        _store.RequestHint();
        _store.RequestHint();
        _store.RequestHint();
        Assert.Equal(HintLevel.Draft, _store.Hint.Value);

        Open("Send", replace: true);
        Assert.Equal(HintLevel.Intent, _store.Hint.Value);
    }

    [Fact]
    public void Narration_StreamsOntoOneMessagePerTurn()
    {
        _store.AppendNarration("  Let's ");
        _store.AppendNarration("start.");
        _store.CloseNarration();
        _store.AppendNarration("Next.");

        var narrations = _store.Messages.OfType<PairingMessage.Narration>().Select(n => n.Text.Value).ToList();
        Assert.Equal(["Let's start.", "Next."], narrations);
    }

    [Fact]
    public void Roadmap_MarksWhatARevisionAddedAndDropped()
    {
        _store.SetRoadmap([new Milestone("Model", false), new Milestone("Store", false)]);
        Assert.All(_store.Roadmap.Value, e => Assert.Equal(RoadmapChange.Kept, e.Change));

        _store.SetRoadmap([new Milestone("Model", true), new Milestone("Events", false)]);

        Assert.Equal(
            [
                new RoadmapEntry("Model", true, RoadmapChange.Kept),
                new RoadmapEntry("Events", false, RoadmapChange.Added),
                new RoadmapEntry("Store", false, RoadmapChange.Removed),
            ],
            _store.Roadmap.Value);
    }

    [Fact]
    public void AHint_AboveTheLevelAskedFor_IsRefused()
    {
        Open();

        Assert.NotNull(_store.ShowHint(new PairingHint.Shape("int Multiply(int a, int b)")));

        _store.RequestHint();
        _store.RequestHint();
        Assert.Null(_store.ShowHint(new PairingHint.Shape("int Multiply(int a, int b)")));
        Assert.IsType<PairingHint.Shape>(Assert.Single(_presentation.Hinted).Code);
    }

    [Fact]
    public void Hints_Accumulate_AndGoWhenTheStopCloses()
    {
        Open();
        _store.RequestHint();
        _store.RequestHint();
        _store.RequestHint();

        _store.ShowHint(new PairingHint.Location([new LineSpan(10, 12)]));
        _store.ShowHint(new PairingHint.Draft("return a * b;"));

        var last = _presentation.Hinted[^1];
        Assert.Equal([new LineSpan(10, 12)], last.Spotlights);
        Assert.IsType<PairingHint.Draft>(last.Code);
        Assert.Equal(2, _store.Hints.Value.Count);

        _store.TakeDraft();
        Assert.Contains("take draft", _presentation.Calls);

        _presentation.Calls.Clear();
        _store.Skip();
        Assert.Contains("clear hints", _presentation.Calls);
        Assert.Empty(_store.Hints.Value);
    }

    [Fact]
    public void WhatTheUserSaysBeforeDone_ReachesTheAgentAheadOfTheDiff()
    {
        Open();
        _store.Say("I went with an event instead of a callback");
        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");

        var said = Assert.IsType<PairingAction.Message>(_store.WaitAsync(CancellationToken.None).Result);
        Assert.Equal("I went with an event instead of a callback", said.Text);
        Assert.IsType<PairingAction.Done>(_store.WaitAsync(CancellationToken.None).Result);
    }

    [Fact]
    public void MovingOnFromAStop_StartsTheConversationAfresh()
    {
        Open();
        _store.Say("Why here?");
        _store.AppendNarration("Because it's the entry point.");
        Assert.Equal(2, _store.Messages.Count);

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");

        Assert.Empty(_store.Messages);
        _store.AppendNarration("Next, the caller.");
        Assert.Single(_store.Messages);
    }

    [Fact]
    public void Skip_StartsTheConversationAfresh_ButKeepsAQuestionStillWaiting()
    {
        Open();
        _store.Say("Skip this one");
        var approval = _store.AskPermission("Switch mode", "SwitchMode");

        _store.Skip();

        var kept = Assert.Single(_store.Messages);
        Assert.Same(approval, Assert.IsType<PairingMessage.Approval>(kept).Pending);
    }

    [Fact]
    public void Show_MovesTheEditor_AndLeavesTheOpenStopAlone()
    {
        var stop = Open();
        _presentation.Calls.Clear();

        var shown = Await(_store.ShowAsync("tests/ClientTests.cs", "RetriesOnce", null, CancellationToken.None), "the show");

        Assert.Equal(10, Assert.IsType<Showing.Shown>(shown).Line);
        Assert.Equal(["show tests/ClientTests.cs#RetriesOnce", "reveal"], _presentation.Calls);
        Assert.Same(stop, _store.Stop.Value);
    }

    [Fact]
    public void Show_WithoutASymbol_OpensTheFileAtTheLine()
    {
        var shown = Await(_store.ShowAsync("src/Client.cs", null, 42, CancellationToken.None), "the show");

        Assert.Equal(42, Assert.IsType<Showing.Shown>(shown).Line);
        Assert.Equal(["show file src/Client.cs:42"], _presentation.Calls);
    }

    [Fact]
    public void Show_RefusesAFileThatIsNotThere()
    {
        var shown = Await(_store.ShowAsync("missing.cs", null, null, CancellationToken.None), "the show");

        Assert.Contains("missing.cs", Assert.IsType<Showing.Refused>(shown).Message);
    }
}
