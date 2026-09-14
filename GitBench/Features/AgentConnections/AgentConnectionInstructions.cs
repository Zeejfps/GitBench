using GitBench.Features.Assistant.Tools;
using GitBench.Features.Review.Walkthrough;
using McpSdk.Protocol.Models;
using McpSdk.Server;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// What the server tells a connecting agent about itself: the review walkthrough protocol, as
/// the model should follow it. Sent as <c>instructions</c> on initialize and restated by the
/// <c>walkthrough</c> prompt.
/// </summary>
internal static class AgentConnectionInstructions
{
    public static readonly string Text =
        "DiffDino is a git client with a review window. These tools read the branch under review "
        + "and drive that window, so you can walk the reviewer through a change: jump to a line, "
        + "light up the lines that matter, and show a card explaining them.\n"
        + "\n"
        + "Addressing a repository:\n"
        + "- Pass repo on every call: your working directory is the right value. A path inside a "
        + "worktree selects that worktree. Without it, the most recently driven review window's "
        + "repository is assumed, then the main window's active one.\n"
        + "\n"
        + "Before pointing at anything:\n"
        + "- Call review_state first. It lists the open review windows for the repository, the range "
        + "each shows, the active file, the lines on screen, the files marked Viewed, and the "
        + "reviewer's current text selection.\n"
        + "- If no window is open, call review_open (head defaults to the checked-out branch).\n"
        + "- get_review_stack lists the files the range touches; get_review_diff gives one file's "
        + "diff. " + ReadTools.LineFormat + " Those numbers are the ones review_focus, "
        + "review_spotlight and walkthrough steps take (side \"new\" unless you say \"old\").\n"
        + "- Every focus and spotlight returns the text of the line it resolved. Read it back and "
        + "correct yourself when it is not the line you meant.\n"
        + "\n"
        + "Walking the reviewer through the change:\n"
        + "- walkthrough_step takes a batch of steps — a title, a markdown body, a focus and "
        + "spotlights each. Send two to four steps per call; the reviewer walks the batch with Next "
        + "and Back locally, and a later call appends to the same walkthrough.\n"
        + $"- The call blocks for at most {(int)ReviewWalkthroughStore.WaitTimeout.TotalSeconds} seconds. "
        + "It returns {action:\"next\"} when the reviewer steps past the last step you sent, "
        + "{action:\"ask\", question, selection?} when they type a question (selection is what they "
        + "had selected in the diff, when anything), or {action:\"pending\"} when the wait ran out — "
        + "then call walkthrough_wait to keep waiting. {action:\"cancelled\"} means the walkthrough "
        + "was ended or superseded. 'at' is the step the reviewer is on, numbered from 1.\n"
        + "- Answer a question with more steps, or with prose in the next step's body.\n"
        + "- Call walkthrough_end(summary_md) when you have shown the last stop or the reviewer "
        + "asks to stop; the summary stays up as the closing card.\n"
        + "- After a walkthrough call returns, make your next walkthrough call within "
        + $"{(int)AgentSession.PresenceTimeout.TotalSeconds} seconds, or the rail reports you as "
        + "disconnected.\n"
        + "\n"
        + "Marking files:\n"
        + "- mark_viewed ticks the reviewer's own Viewed checkbox. Only mark files you have "
        + "actually read through.";
}

/// <summary>The one prompt the server offers: ask for a walkthrough of the change under review.</summary>
internal sealed class WalkthroughPrompt : IPromptController
{
    public const string Name = "walkthrough";
    public const string FocusArgument = "focus";

    private static readonly Prompt Definition = new(
        Name,
        "Walk me through the change under review in DiffDino, step by step.",
        [new PromptArgument(FocusArgument, "What to concentrate on: a file, a concern, a question.", required: false)]);

    public bool IsListChangedNotificationSupported => false;

    // Never raised: the prompt list is fixed.
    public event Action? ListChanged
    {
        add { }
        remove { }
    }

    public Task<ListPromptsResult> ListPrompts(ListPromptsRequest request, McpRequestContext context) =>
        Task.FromResult(new ListPromptsResult([Definition], null));

    public Task<GetPromptResult> GetPrompt(GetPromptRequest request, McpRequestContext context)
    {
        if (request.Name != Name)
            throw new InvalidOperationException($"Unknown prompt '{request.Name}'.");

        var focus = FocusOf(request);
        var text =
            "Walk me through the change under review in DiffDino, step by step, using the walkthrough "
            + "tools: read the review with review_state, get_review_stack and get_review_diff, then send "
            + "walkthrough_step batches of two to four stops — each focused on the lines that matter, "
            + "spotlit, with a card that says what the code does and how it connects to the rest of the "
            + "codebase — and answer my questions as they come back. Finish with walkthrough_end and a "
            + "short summary."
            + (focus is { Length: > 0 } ? $" Concentrate on: {focus}" : string.Empty);

        return Task.FromResult(new GetPromptResult(
            [new PromptMessage("user", new TextContent(text))],
            Definition.Description,
            null));
    }

    private static string? FocusOf(GetPromptRequest request)
    {
        if (request.Arguments is not { } arguments) return null;
        foreach (var argument in arguments)
            if (argument.Key == FocusArgument && argument.Value.IsString)
                return argument.Value.AsString().Trim();
        return null;
    }
}
