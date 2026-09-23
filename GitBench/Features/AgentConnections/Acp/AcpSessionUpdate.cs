using System.Text.Json.Nodes;

namespace GitBench.Features.AgentConnections.Acp;

/// <summary>What a <c>session/update</c> notification says, in the cases the app shows.</summary>
internal abstract record AcpSessionUpdate
{
    /// <summary>A fragment of the agent's reply to the user.</summary>
    public sealed record MessageChunk(string Text) : AcpSessionUpdate;

    /// <summary>A fragment of the agent's reasoning.</summary>
    public sealed record ThoughtChunk(string Text) : AcpSessionUpdate;

    /// <summary>A tool call started, or changed title or status.</summary>
    public sealed record ToolCall(string Id, string? Title, AcpToolKind? Kind, AcpToolCallStatus? Status) : AcpSessionUpdate;

    /// <summary>The agent's own plan, replaced whole.</summary>
    public sealed record Plan(IReadOnlyList<string> Entries) : AcpSessionUpdate;

    /// <summary>Anything else: usage, commands, session info.</summary>
    public sealed record Other(string Kind) : AcpSessionUpdate;

    public static AcpSessionUpdate? Parse(JsonNode? update)
    {
        if (update is not JsonObject body || StringOf(body["sessionUpdate"]) is not { } kind) return null;
        switch (kind)
        {
            case "agent_message_chunk":
                return new MessageChunk(ContentText(body["content"]));
            case "agent_thought_chunk":
                return new ThoughtChunk(ContentText(body["content"]));
            case "tool_call":
            case "tool_call_update":
                if (StringOf(body["toolCallId"]) is not { } id) return new Other(kind);
                return new ToolCall(
                    id,
                    StringOf(body["title"]),
                    StringOf(body["kind"]) is { } toolKind ? AcpPermissionPolicy.ParseToolKind(toolKind) : null,
                    ParseStatus(StringOf(body["status"])));
            case "plan":
                var entries = new List<string>();
                if (body["entries"] is JsonArray list)
                    foreach (var entry in list)
                        if (StringOf(entry?["content"]) is { } content)
                            entries.Add(content);
                return new Plan(entries);
            default:
                return new Other(kind);
        }
    }

    private static AcpToolCallStatus? ParseStatus(string? status) => status switch
    {
        "pending" => AcpToolCallStatus.Pending,
        "in_progress" => AcpToolCallStatus.InProgress,
        "completed" => AcpToolCallStatus.Completed,
        "failed" => AcpToolCallStatus.Failed,
        _ => null,
    };

    private static string ContentText(JsonNode? content) =>
        StringOf(content?["type"]) == "text" ? StringOf(content?["text"]) ?? string.Empty : string.Empty;

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

internal enum AcpToolCallStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>Why a prompt turn ended.</summary>
internal enum AcpStopReason
{
    EndTurn,
    MaxTokens,
    MaxTurnRequests,
    Refusal,
    Cancelled,
}
