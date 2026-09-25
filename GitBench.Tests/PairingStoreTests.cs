using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>The pairing loop's hand-off: one open stop at a time, Done handing the agent the user's
/// diff since the stop was shown, and every move going to the agent in the order it was made, with
/// nothing waiting on the user in between.</summary>
public sealed class PairingStoreTests : IDisposable
{
    private readonly RecordingPairingPresentation _presentation = new();
    private readonly ScriptedWorkspace _workspace = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly AgentTranscript _transcript = new();
    private readonly List<PairingAction> _moves = new();
    private readonly PairingStore _store;

    public PairingStoreTests()
    {
        _store = new PairingStore("Add a retry", "Claude Code", _transcript, _presentation, _workspace, _dispatcher, _moves.Add);
        _store.MarkRunning();
    }

    public void Dispose() => _store.Dispose();

    private T Await<T>(Task<T> task, string what)
    {
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);
        return task.Result;
    }

    private static readonly DraftRequest Draft = new("public void Fetch() => Retry(Send);", new DraftSpan.Declaration());

    private OpenStop Open(string symbol = "Fetch", bool replace = false, DraftRequest? draft = null)
    {
        var opening = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", symbol, null), "Retry " + symbol, "Because.", draft ?? Draft, replace, CancellationToken.None),
            "the stop to open");
        return Assert.IsType<StopOpening.Opened>(opening).Stop;
    }

    [Fact]
    public void OpenStop_PlacesItAndTheAgentsCodeInTheEditor_AndNumbersIt()
    {
        var stop = Open();

        Assert.Equal(1, stop.Stop.Number);
        Assert.Equal(["show src/Client.cs#Fetch", "reveal draft", "show draft"], _presentation.Calls);
        Assert.Same(stop, _store.Stop.Value);
        Assert.Equal(new DraftPlace.Replace(new LineSpan(10, 14)), Assert.Single(_presentation.Drafts).Place);
    }

    [Fact]
    public void TheAgentsCode_GoesOnTheLinesItNames()
    {
        var stop = Open(draft: new DraftRequest("retry();", new DraftSpan.After(12)));

        Assert.Equal(new StopDraft("retry();", new DraftPlace.InsertAfter(new FileLine(12))), stop.Draft);
    }

    [Fact]
    public void TheAgentsCode_OnLinesTheFileDoesNotHave_IsRefused()
    {
        var opening = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", "Fetch", null), "t", "r",
                new DraftRequest("x", new DraftSpan.Lines(38, 45)), false, CancellationToken.None),
            "the refusal");

        Assert.Contains("40 lines", Assert.IsType<StopOpening.Refused>(opening).Message);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void ASecondStop_IsRefusedWhileOneIsOpen()
    {
        Open();

        var second = Await(
            _store.OpenStopAsync(new StopTarget("src/Client.cs", "Send", null), "t", "r", Draft, false, CancellationToken.None),
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
            _store.OpenStopAsync(new StopTarget("src/Client.cs", "Nope", null), "t", "r", Draft, false, CancellationToken.None),
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

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to reach the agent");

        var action = Assert.IsType<PairingAction.Done>(Assert.Single(_moves));
        Assert.Equal(1, action.Stop);
        Assert.Equal("+ retry", action.Diff);
        Assert.Contains("save", _presentation.Calls);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void Moves_GoOutInOrder_SoAMessageNeverHidesADone()
    {
        Open();
        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");
        _store.Say("Why a loop?");

        Assert.Equal(2, _moves.Count);
        Assert.IsType<PairingAction.Done>(_moves[0]);
        Assert.Equal("Why a loop?", Assert.IsType<PairingAction.Message>(_moves[1]).Text);
    }

    [Fact]
    public void Say_CarriesTheCaret()
    {
        Open();
        _presentation.CaretState.Value = new EditorCaret("C:/repo/src/Client.cs", TextPosition.At(12, 3), "retries");

        _store.Say("Is this the right place?");

        var ask = Assert.IsType<PairingAction.Message>(Assert.Single(_moves));
        Assert.Equal("retries", ask.Caret!.SelectedText);
        Assert.Contains(_transcript.Messages, m => m is PairingMessage.FromUser { Text: "Is this the right place?" });
    }

    [Fact]
    public void ATurnEnding_HandsTheMoveToTheUser_AndTheirMoveHandsItBack()
    {
        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);
        _store.MarkTurnStarted();
        _store.MarkTurnEnded();
        Assert.Equal(new PairingPhase.Running(true), _store.Phase.Value);

        _store.Say("?");

        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);
    }

    [Fact]
    public void WhileATurnRuns_AStopDoesNotHandOverUntilTheTurnEnds()
    {
        _store.MarkTurnStarted();

        Open();

        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);
        Assert.True(_store.HasHandedOver);
    }

    [Fact]
    public void AnAgentWhoseTurnsAreNotSeen_HandsOverWithAStopOrAReply()
    {
        Open();
        Assert.Equal(new PairingPhase.Running(true), _store.Phase.Value);

        _store.Skip();
        Assert.Equal(new PairingPhase.Running(false), _store.Phase.Value);

        _store.AddReply("Fair enough.");
        Assert.Equal(new PairingPhase.Running(true), _store.Phase.Value);
    }

    [Fact]
    public void ATurnThatLeavesNeitherAStopNorAReply_HasNotHandedOver()
    {
        _store.MarkTurnStarted();
        _store.SetRoadmap([new Milestone("Model", false)]);
        _store.MarkTurnEnded();
        Assert.False(_store.HasHandedOver);

        _store.MarkTurnStarted();
        _store.AddReply("Where should the retry live?");

        Assert.True(_store.HasHandedOver);
    }

    [Fact]
    public void EndByUser_TellsTheAgentOnce()
    {
        Open();

        _store.EndByUser();
        _store.EndByUser();

        Assert.IsType<PairingAction.Ended>(Assert.Single(_moves));
        Assert.IsType<PairingPhase.Ended>(_store.Phase.Value);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void Skip_ClosesTheStopWithoutADiff()
    {
        Open();

        _store.Skip();

        Assert.IsType<PairingAction.Skipped>(Assert.Single(_moves));
        Assert.Null(_store.Stop.Value);
        Assert.Equal(1, _workspace.Captures);
    }

    [Fact]
    public void Accept_PutsTheAgentsCodeIn_AndStaysOnTheStop()
    {
        Open();

        Assert.True(Await(_store.AcceptAsync(), "Accept"));

        Assert.Empty(_moves);
        Assert.IsType<DraftState.Taken>(_store.Stop.Value!.DraftState);
        Assert.Contains("take draft", _presentation.Calls);
        Assert.Contains("clear draft", _presentation.Calls);
    }

    [Fact]
    public void AcceptAndNext_PutsTheCodeIn_AndMovesOn_AsAccepted()
    {
        _workspace.Current = "before";
        Open();
        _presentation.Calls.Clear();

        _workspace.Current = "after";
        var go = _store.AcceptAndNextAsync();
        Pump.WaitFor(_dispatcher, () => go.IsCompleted, "Accept & next to reach the agent");

        var done = Assert.IsType<PairingAction.Done>(Assert.Single(_moves));
        Assert.Equal(DraftOutcome.AcceptedAsIs, done.Draft);
        Assert.Equal("diff before..after", done.Diff);
        Assert.True(_presentation.Calls.IndexOf("take draft") < _presentation.Calls.IndexOf("save"));
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void AcceptedCodeChangedBeforeNext_ReachesTheAgentAsEdited()
    {
        Open();
        Await(_store.AcceptAsync(), "Accept");
        _presentation.FileText = "file, changed by hand";

        var next = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => next.IsCompleted, "Next to reach the agent");

        Assert.Equal(DraftOutcome.AcceptedThenEdited, Assert.IsType<PairingAction.Done>(Assert.Single(_moves)).Draft);
    }

    [Fact]
    public void TheEditorsPills_AcceptOrAcceptAndMoveOn()
    {
        Open();

        _presentation.Actions!.AcceptAndNext();
        Pump.WaitFor(_dispatcher, () => _moves.Count > 0, "the pill to reach the agent");

        Assert.Equal(DraftOutcome.AcceptedAsIs, Assert.IsType<PairingAction.Done>(Assert.Single(_moves)).Draft);
    }

    [Fact]
    public void Accept_ThatCannotPutTheCodeIn_LeavesTheStopOpen()
    {
        Open();
        _presentation.TakeSucceeds = false;

        Await(_store.AcceptAsync().ContinueWith(_ => true), "Accept to give up");

        Assert.Empty(_moves);
        Assert.NotNull(_store.Stop.Value);
        Assert.Equal(StopActivity.Idle, _store.Activity.Value);
        Assert.Equal(NoticeTone.Error, Assert.IsType<PairingMessage.Notice>(Assert.Single(_transcript.Messages)).Tone);
    }

    [Fact]
    public void ANewFileStop_CreatesTheFileEmpty_AndItsCodeIsTheFirstBlock()
    {
        _presentation.Answer = t => new StopPlacement.Placed(new StopLocation.NewFile("C:/repo/" + t.Path));

        var stop = Open(draft: new DraftRequest("class Retry {}", new DraftSpan.Declaration()));

        Assert.Equal(string.Empty, _workspace.Files["src/Client.cs"]);
        Assert.Equal(new DraftPlace.Replace(new LineSpan(1, 1)), stop.Draft.Place);
        Assert.Contains("show draft", _presentation.Calls);
    }

    [Fact]
    public void ANewFile_LeftEmpty_GoesWhenTheStopIsSkipped()
    {
        _presentation.Answer = t => new StopPlacement.Placed(new StopLocation.NewFile("C:/repo/" + t.Path));
        Open(draft: new DraftRequest("class Retry {}", new DraftSpan.Declaration()));

        _store.Skip();

        Assert.False(_workspace.Files.ContainsKey("src/Client.cs"));
    }

    [Fact]
    public void AnEmptyFileThatWasAlreadyThere_StaysWhenTheStopIsSkipped()
    {
        _workspace.Files["src/Client.cs"] = string.Empty;
        _presentation.Answer = t => new StopPlacement.Placed(new StopLocation.NewFile("C:/repo/" + t.Path));
        Open(draft: new DraftRequest("class Retry {}", new DraftSpan.Declaration()));

        _store.Skip();

        Assert.True(_workspace.Files.ContainsKey("src/Client.cs"));
    }

    [Fact]
    public void Done_WithoutAccepting_SaysTheUserTypedIt()
    {
        Open();

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to reach the agent");

        Assert.Equal(DraftOutcome.NotAccepted, Assert.IsType<PairingAction.Done>(Assert.Single(_moves)).Draft);
        Assert.Contains("clear draft", _presentation.Calls);
    }

    [Fact]
    public void Narration_StreamsOntoOneMessagePerTurn()
    {
        _transcript.AppendNarration("  Let's ");
        _transcript.AppendNarration("start.");
        _transcript.CloseNarration();
        _transcript.AppendNarration("Next.");

        var narrations = _transcript.Messages.OfType<PairingMessage.Narration>().Select(n => n.Text.Value).ToList();
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
    public void WhatTheUserSaysBeforeDone_ReachesTheAgentAheadOfTheDiff()
    {
        Open();
        _store.Say("I went with an event instead of a callback");
        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");

        Assert.Equal(2, _moves.Count);
        Assert.Equal("I went with an event instead of a callback", Assert.IsType<PairingAction.Message>(_moves[0]).Text);
        Assert.IsType<PairingAction.Done>(_moves[1]);
    }

    [Fact]
    public void MovingOnFromAStop_KeepsTheConversation()
    {
        Open();
        _store.Say("Why here?");
        _transcript.AppendNarration("Because it's the entry point.");

        var done = _store.DoneAsync();
        Pump.WaitFor(_dispatcher, () => done.IsCompleted, "Done to finish");

        Assert.Contains(_transcript.Messages, m => m is PairingMessage.FromUser { Text: "Why here?" });
        Assert.Contains(_transcript.Messages, m => m is PairingMessage.Narration { Text.Value: "Because it's the entry point." });
    }

    [Fact]
    public void Skip_KeepsTheConversation()
    {
        Open();
        _store.Say("Skip this one");

        _store.Skip();

        Assert.Contains(_transcript.Messages, m => m is PairingMessage.FromUser { Text: "Skip this one" });
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

public sealed class DraftPlacingTests
{
    private static readonly StopLocation Stop = new StopLocation.OnSymbol(
        "C:/repo/a.ts", TextPosition.At(26, 0), "line 26", new FileLine(27), RecordingPairingPresentation.FileLines);

    private static StopDraft Place(string code, DraftSpan? span = null) =>
        Assert.IsType<DraftPlacement.Placed>(DraftPlacing.Place(Stop, new DraftRequest(code, span ?? new DraftSpan.Declaration()))).Draft;

    [Fact]
    public void LinesThatReadTheSame_AreLeftOut_SoAddedLinesGoInAsAnInsertion()
    {
        var draft = Place("line 26\nline 27\nauthorId?: string");

        Assert.Equal(new StopDraft("authorId?: string", new DraftPlace.InsertAfter(new FileLine(27))), draft);
    }

    [Fact]
    public void OnlyTheLinesThatChange_AreReplaced()
    {
        var draft = Place("line 20\nchanged\nline 22", new DraftSpan.Lines(20, 22));

        Assert.Equal(new StopDraft("changed", new DraftPlace.Replace(new LineSpan(21, 21))), draft);
    }

    [Fact]
    public void LinesAddedAboveTheFirstLine_KeepItInTheReplacement()
    {
        var draft = Place("import x\nline 1", new DraftSpan.Lines(1, 1));

        Assert.Equal(new StopDraft("import x\nline 1", new DraftPlace.Replace(new LineSpan(1, 1))), draft);
    }

    [Fact]
    public void CodeThatChangesNothing_IsRefused()
    {
        var placement = DraftPlacing.Place(Stop, new DraftRequest("line 26\nline 27", new DraftSpan.Declaration()));

        Assert.Contains("changes nothing", Assert.IsType<DraftPlacement.Refused>(placement).Message);
    }
}
