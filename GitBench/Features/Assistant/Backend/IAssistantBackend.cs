using System.Text.Json;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// The seam between the agent loop and whatever produces model output — the Messages API today,
/// a Claude Code CLI later.
internal interface IAssistantBackend
{
    IAsyncEnumerable<BackendEvent> SendAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        CancellationToken ct);
}

/// One streamed step of a model turn. Every stream ends with exactly one terminal event:
/// TurnComplete, Refusal or Error.
internal abstract record BackendEvent
{
    private BackendEvent() { }

    public sealed record TextDelta(string Text) : BackendEvent;

    // Thinking display defaults to omitted, so this carries no content — only the fact that the
    // model started reasoning.
    public sealed record Thinking : BackendEvent;

    public sealed record ToolUse(string Id, string Name, JsonElement Input) : BackendEvent;

    public sealed record TurnComplete(StopReason Reason) : BackendEvent;

    public sealed record Refusal(string? Category, string? Explanation) : BackendEvent;

    public sealed record Error(string Message, string? Detail = null) : BackendEvent;
}

internal enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    PauseTurn,
    Other,
}
