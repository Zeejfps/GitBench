using System.Text.Json;

namespace GitBench.Features.Assistant.Backend;

/// One request to the model: which tier answers it, the cached system prompt, and the
/// conversation so far.
internal sealed record AssistantTurn(
    ModelTier Tier,
    string SystemPrompt,
    IReadOnlyList<AssistantMessage> Messages)
{
    // Caps thinking plus response text together, so it needs headroom well past the visible answer.
    public const int DefaultMaxTokens = 64000;

    public int MaxTokens { get; init; } = DefaultMaxTokens;
}

/// One entry of the conversation. RepoContext is the live repo-state block: it renders as a
/// mid-conversation system entry so the cached tools+system prefix survives between turns.
internal abstract record AssistantMessage
{
    private AssistantMessage() { }

    public sealed record User(string Text) : AssistantMessage;

    public sealed record Assistant(IReadOnlyList<AssistantContent> Content) : AssistantMessage;

    public sealed record ToolResults(IReadOnlyList<AssistantToolResult> Results) : AssistantMessage;

    public sealed record RepoContext(string Text) : AssistantMessage;
}

internal abstract record AssistantContent
{
    private AssistantContent() { }

    public sealed record Text(string Value) : AssistantContent;

    public sealed record ToolUse(string Id, string Name, JsonElement Input) : AssistantContent;
}

internal sealed record AssistantToolResult(string ToolUseId, string Content, bool IsError);
