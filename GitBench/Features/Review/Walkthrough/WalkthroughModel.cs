using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>Where a narrator runs: a terminal agent over the MCP server, or the built-in assistant.</summary>
internal enum NarratorKind
{
    McpSession,
    Assistant,
}

/// <summary>Who is driving a walkthrough, as the rail names it.</summary>
internal sealed record Narrator(NarratorKind Kind, string Label)
{
    public static readonly Narrator TerminalAgent = new(NarratorKind.McpSession, "Terminal agent");
    public static readonly Narrator Assistant = new(NarratorKind.Assistant, "Assistant");
}

/// <summary>What the walkthrough is doing: nothing, being narrated (and whether the narrator is
/// currently waiting on the reviewer), or abandoned by a narrator that went away.</summary>
internal abstract record WalkthroughPhase
{
    public sealed record Idle : WalkthroughPhase;

    /// <summary><paramref name="Waiting"/> is true while a wait call is attached — the reviewer's
    /// next step past the frontier goes straight to the narrator — and false in the gaps between
    /// calls, when the narrator is still composing the next batch.</summary>
    public sealed record Narrating(Narrator Who, bool Waiting) : WalkthroughPhase;

    public sealed record Disconnected(Narrator Who) : WalkthroughPhase;
}

/// <summary>What a wait on the reviewer came back with. <c>At</c> is the 0-based index of the step
/// the reviewer was on when it happened, or -1 when no step was showing.</summary>
internal abstract record WalkthroughAction(int At)
{
    /// <summary>The reviewer stepped past the last step the narrator sent.</summary>
    public sealed record Next(int At) : WalkthroughAction(At);

    /// <summary>The reviewer typed a question, with whatever they had selected in the diff.</summary>
    public sealed record Ask(int At, string Question, DiffSelectionQuote? Selection) : WalkthroughAction(At);

    /// <summary>The bounded wait ran out; the narrator should wait again.</summary>
    public sealed record Pending(int At) : WalkthroughAction(At);

    /// <summary>The wait was superseded — a newer wait, a new batch, the end of the walkthrough, a
    /// disconnect, or the caller's own cancellation.</summary>
    public sealed record Cancelled(int At) : WalkthroughAction(At);
}

/// <summary>What wakes the built-in assistant as a narrator. It has no wait to answer, so the
/// reviewer's moves reach it as its next user turn: asking for a walkthrough in the first place,
/// stepping past the last step it sent, or asking a question about the step they are on.</summary>
internal abstract record WalkthroughCue
{
    /// <summary>The reviewer asked to be walked through the window's change.</summary>
    public sealed record Begin : WalkthroughCue;

    /// <summary>A move mid-walkthrough, at the 0-based step the reviewer was on — the only cues the
    /// store itself raises.</summary>
    public abstract record Move(int At) : WalkthroughCue;

    /// <summary>The reviewer stepped past the frontier.</summary>
    public sealed record Next(int At) : Move(At);

    /// <summary>The reviewer typed a question, with whatever they had selected in the diff.</summary>
    public sealed record Ask(int At, string Question, DiffSelectionQuote? Selection) : Move(At);
}

/// <summary>What the rail shows: nothing, the wait for a narrator's first step, one step of the
/// walkthrough, or the narrator's closing summary once it ended.</summary>
internal abstract record WalkthroughCard
{
    public sealed record None : WalkthroughCard;

    /// <summary>A walkthrough was asked for and no step has arrived yet; whatever the narrator says
    /// meanwhile is the card's footnote.</summary>
    public sealed record Preparing : WalkthroughCard;

    public sealed record Step(int Index, int Count, WalkthroughStep Content) : WalkthroughCard;

    public sealed record Finished(string SummaryMarkdown) : WalkthroughCard;
}

/// <summary>One entry of the exchange under a step: the reviewer's question, the narrator's prose
/// outside its tool calls, or a turn that did not work out. The exchange reads as the chat does,
/// so each is rendered through the assistant transcript's rows.</summary>
internal abstract record WalkthroughMessage
{
    /// <summary>What the reviewer asked, with the selection that rode along.</summary>
    public sealed record Question(string Text, DiffSelectionQuote? Selection) : WalkthroughMessage;

    /// <summary>The narrator's prose, growing as it streams.</summary>
    public sealed record Narration(IReadable<string> Text) : WalkthroughMessage;

    /// <summary>A turn that died: what went wrong.</summary>
    public sealed record Failure(string Text) : WalkthroughMessage;

    /// <summary>A turn the narrator declined, with its explanation.</summary>
    public sealed record Refusal(string Text) : WalkthroughMessage;
}
