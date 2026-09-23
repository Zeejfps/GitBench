using GitBench.Features.Assistant;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>Where a stop sends the user: a repo-relative file and a declaration in it, named rather
/// than numbered so it survives edits. A declaration that doesn't exist yet names the one it goes
/// after.</summary>
internal sealed record StopTarget(string Path, string Symbol, string? After);

/// <summary>One stop on the loop, numbered from 1 in the order the agent sent them.</summary>
internal sealed record PairingStop(int Number, StopTarget Target, string Title, string Reason);

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
    /// <summary>On the declaration's name. <paramref name="LastLine"/> is where the declaration
    /// ends; <paramref name="Lines"/> are the file's, as it was found.</summary>
    public sealed record OnSymbol(string AbsolutePath, TextPosition At, string LineText, FileLine LastLine, IReadOnlyList<string> Lines) : StopLocation;

    /// <summary>A declaration still to be written: the end of the one it goes after.</summary>
    public sealed record Insertion(string AbsolutePath, TextPosition At, string After, IReadOnlyList<string> Lines) : StopLocation;

    /// <summary>The file doesn't exist yet, or has nothing in it: the stop opens it empty and the
    /// agent's code is its first block.</summary>
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

/// <summary>Where the agent asked its code for a stop to go, before it is checked against the file.</summary>
internal abstract record DraftSpan
{
    /// <summary>In place of the stop's declaration, after the one it names, or as the new file.</summary>
    public sealed record Declaration : DraftSpan;

    /// <summary>In place of these lines, 1-based and inclusive.</summary>
    public sealed record Lines(int From, int To) : DraftSpan;

    /// <summary>In after this line, 1-based.</summary>
    public sealed record After(int Line) : DraftSpan;
}

/// <summary>The agent's code for a stop, as it sent it.</summary>
internal sealed record DraftRequest(string Code, DraftSpan Span);

/// <summary>Where the agent's code for a stop goes in the file.</summary>
internal abstract record DraftPlace
{
    public sealed record Replace(LineSpan Lines) : DraftPlace;

    public sealed record InsertAfter(FileLine Line) : DraftPlace;
}

/// <summary>The agent's code for one stop — one block — and where it goes. The user accepts it
/// or types it themselves.</summary>
internal sealed record StopDraft(string Code, DraftPlace Place);

/// <summary>Where the agent's code for the open stop stands.</summary>
internal abstract record DraftState
{
    /// <summary>Shown in the editor, not taken.</summary>
    public sealed record Offered : DraftState;

    /// <summary>Taken into the file, which read <paramref name="FileText"/> right after — null
    /// where it could not be read.</summary>
    public sealed record Taken(string? FileText) : DraftState;
}

/// <summary>What the user did with the agent's code by the time they moved on.</summary>
internal enum DraftOutcome
{
    NotAccepted,
    AcceptedAsIs,
    AcceptedThenEdited,
}

/// <summary>The stop the user is on, and what it has come to. <paramref name="CreatedFile"/>: the
/// empty file the stop created, taken back out if the user moves on without writing anything into it.</summary>
internal sealed record OpenStop(
    PairingStop Stop, StopLocation Location, TreeSnapshot Baseline, StopDraft Draft, DraftState DraftState, string? CreatedFile);

/// <summary>What the stop card is busy with, if anything.</summary>
internal enum StopActivity
{
    Idle,
    Checking,
    Accepting,
}

/// <summary>What a wait on the user came back with. <c>Stop</c> is the number of the stop it was
/// about, or 0 when none was open.</summary>
internal abstract record PairingAction(int Stop)
{
    /// <summary>The user finished the stop: their diff since it was shown, what they did with the
    /// agent's code. Anything they wanted to say about it they said in the conversation.</summary>
    public sealed record Done(int Stop, string Diff, DraftOutcome Draft, IReadOnlyList<string> Problems) : PairingAction(Stop);

    /// <summary>The user said something to the agent — a question, or what they did instead — with
    /// where their caret was.</summary>
    public sealed record Message(int Stop, string Text, EditorCaret? Caret) : PairingAction(Stop);

    /// <summary>The user passed on the stop without changing anything for it.</summary>
    public sealed record Skipped(int Stop) : PairingAction(Stop);

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
