using GitBench.Features.Assistant;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>What the user does at a stop: change the code, or first see a test the agent wrote go
/// red and then make it pass.</summary>
internal enum PairingStopKind
{
    Edit,
    Test,
}

/// <summary>Where a stop sends the user: a repo-relative file and a declaration in it, named rather
/// than numbered so it survives edits. A declaration that doesn't exist yet names the one it goes
/// after.</summary>
internal sealed record StopTarget(string Path, string Symbol, string? After);

/// <summary>One stop on the loop, numbered from 1 in the order the agent sent them.</summary>
internal sealed record PairingStop(int Number, StopTarget Target, string Title, string Reason, PairingStopKind Kind);

/// <summary>A coarse step of the roadmap, as the agent sent it.</summary>
internal sealed record Milestone(string Title, bool Done);

/// <summary>How a roadmap entry compares with the roadmap before the agent's last revision.</summary>
internal enum RoadmapChange
{
    Kept,
    Added,
    Removed,
}

internal sealed record RoadmapEntry(string Title, bool Done, RoadmapChange Change);

/// <summary>What the session is doing.</summary>
internal abstract record PairingPhase
{
    /// <summary>The agent is being started and has not yet sent anything.</summary>
    public sealed record Starting : PairingPhase;

    /// <summary>The agent is driving. <paramref name="Waiting"/> is true while a wait is attached —
    /// the user's move goes straight to the agent — and false while it is thinking.</summary>
    public sealed record Running(bool Waiting) : PairingPhase;

    /// <summary>The agent stopped calling back without ending the session.</summary>
    public sealed record Disconnected(string Reason) : PairingPhase;

    /// <summary>Finished, by the agent (with its summary) or by the user.</summary>
    public sealed record Ended(string? Summary) : PairingPhase;

    /// <summary>The agent could not be started, or died.</summary>
    public sealed record Failed(string Reason) : PairingPhase;
}

/// <summary>Where a stop landed in the editor.</summary>
internal abstract record StopLocation
{
    /// <summary>On the declaration's name.</summary>
    public sealed record OnSymbol(string AbsolutePath, TextPosition At, string LineText) : StopLocation;

    /// <summary>A declaration still to be written: the end of the one it goes after.</summary>
    public sealed record Insertion(string AbsolutePath, TextPosition At, string After) : StopLocation;

    /// <summary>The file doesn't exist yet: the user creates it.</summary>
    public sealed record NewFile(string AbsolutePath) : StopLocation;
}

/// <summary>Why a stop could not be placed; the agent is told so and the stop doesn't open.</summary>
internal abstract record StopMiss
{
    public sealed record NoSuchSymbol(string Path, string Symbol, IReadOnlyList<string> Known) : StopMiss;

    public sealed record NoSuchAfter(string Path, string After, IReadOnlyList<string> Known) : StopMiss;

    public sealed record OutsideRepository(string Path) : StopMiss;

    public sealed record Unreadable(string Path, string Reason) : StopMiss;
}

internal abstract record StopPlacement
{
    public sealed record Placed(StopLocation Location) : StopPlacement;

    public sealed record Missed(StopMiss Miss) : StopPlacement;
}

/// <summary>What happened when a test the agent wrote was run.</summary>
internal abstract record TestRun
{
    public sealed record Passed(string Output) : TestRun;

    public sealed record Failed(int ExitCode, string Output) : TestRun;

    /// <summary>The command could not be run at all.</summary>
    public sealed record Unrunnable(string Reason) : TestRun;
}

/// <summary>The stop the user is on, and what it has come to.</summary>
internal sealed record OpenStop(PairingStop Stop, StopLocation Location, TreeSnapshot Baseline, StopTest? Test);

/// <summary>A test stop's test: the file the agent wrote, how to put it back, the test to run,
/// and where it stands.</summary>
internal sealed record StopTest(string Path, string Name, TestFileUndo Undo, TestState State);

/// <summary>What a test file held before the agent wrote it: its bytes, or nothing for a new file.</summary>
internal sealed record TestFileUndo(string RelativePath, byte[]? PriorContent);

/// <summary>Where a stop's test stands.</summary>
internal abstract record TestState
{
    /// <summary>Written and not yet run. The user runs it — the test is code the agent wrote, and
    /// running it is the user's call — with the repository's command, or the agent's suggestion
    /// the first time.</summary>
    public sealed record AwaitingRun(string Command) : TestState;

    public sealed record Running : TestState;

    /// <summary>The test fails. <paramref name="AfterDone"/>: it still failed when the user pressed
    /// Done, so the card offers to close it red.</summary>
    public sealed record Red(TestRun.Failed Run, bool AfterDone) : TestState;

    /// <summary>The command could not run the test.</summary>
    public sealed record Unrunnable(string Reason) : TestState;
}

/// <summary>What the stop card is busy with, if anything.</summary>
internal enum StopActivity
{
    Idle,
    Checking,
    RunningTest,
}

/// <summary>How far the user has asked the agent to go at the open stop.</summary>
internal enum HintLevel
{
    Intent = 0,
    Location = 1,
    Shape = 2,
    Draft = 3,
}

/// <summary>What the agent showed at a hint level above intent.</summary>
internal abstract record PairingHint(HintLevel Level)
{
    /// <summary>The lines to change, lit up in the editor.</summary>
    public sealed record Location(IReadOnlyList<LineSpan> Lines) : PairingHint(HintLevel.Location);

    /// <summary>A signature or pseudocode, drawn as suggested lines at the stop.</summary>
    public sealed record Shape(string Code) : PairingHint(HintLevel.Shape);

    /// <summary>The agent's code for this stop only, drawn as suggested lines that go as the user
    /// types them.</summary>
    public sealed record Draft(string Code) : PairingHint(HintLevel.Draft);
}

/// <summary>What a wait on the user came back with. <c>Stop</c> is the number of the stop it was
/// about, or 0 when none was open.</summary>
internal abstract record PairingAction(int Stop)
{
    /// <summary>The user finished the stop: their diff since it was shown, and for a test stop the
    /// run that closed it. Anything they wanted to say about it they said in the conversation.</summary>
    public sealed record Done(int Stop, string Diff, TestRun? Test, bool Forced, IReadOnlyList<string> Problems)
        : PairingAction(Stop);

    /// <summary>The user said something to the agent — a question, or what they did instead — with
    /// where their caret was.</summary>
    public sealed record Message(int Stop, string Text, EditorCaret? Caret) : PairingAction(Stop);

    /// <summary>The user passed on the stop without changing anything for it.</summary>
    public sealed record Skipped(int Stop) : PairingAction(Stop);

    /// <summary>The user asked for the next hint level at the open stop.</summary>
    public sealed record Hint(int Stop, HintLevel Level) : PairingAction(Stop);

    /// <summary>The test written for the stop was run before the user wrote anything. Red opens
    /// the stop; green means the test proves nothing, and it was taken back out.</summary>
    public sealed record TestRan(int Stop, TestRun Run) : PairingAction(Stop);

    /// <summary>The user took the stop's test back out.</summary>
    public sealed record TestUndone(int Stop) : PairingAction(Stop);

    /// <summary>The user ended the session.</summary>
    public sealed record Ended(int Stop) : PairingAction(Stop);

    /// <summary>The bounded wait ran out; wait again.</summary>
    public sealed record Pending(int Stop) : PairingAction(Stop);

    /// <summary>A newer wait took over, or the caller went away.</summary>
    public sealed record Cancelled(int Stop) : PairingAction(Stop);
}

/// <summary>One entry of the conversation about the open stop — it starts afresh as the user
/// moves on from each one.</summary>
internal abstract record PairingMessage
{
    /// <summary>What the user said.</summary>
    public sealed record FromUser(string Text) : PairingMessage;

    /// <summary>The agent's prose, growing as it streams.</summary>
    public sealed record Narration(IReadable<string> Text) : PairingMessage;

    /// <summary>Something the app did or refused on the session's behalf.</summary>
    public sealed record Notice(string Text, NoticeTone Tone) : PairingMessage;

    /// <summary>A tool call the write guard left to the user, waiting on their answer.</summary>
    public sealed record Approval(PendingToolApproval Pending) : PairingMessage;

}

internal enum NoticeTone
{
    Info,
    Refused,
    Error,
}
