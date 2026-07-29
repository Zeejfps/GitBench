using System.Text.Json;

namespace GitBench.Features.Assistant.Tools;

/// One capability the assistant can invoke, bound to a single repository.
internal interface IAssistantTool
{
    string Name { get; }

    /// What the tool does and when to reach for it — the model sees this verbatim.
    string Description { get; }

    /// The argument schema as a compact JSON object literal, emitted raw into the request.
    string JsonSchema { get; }

    /// Whether invoking this tool changes the repository. Write tools pause the loop for approval.
    bool IsWrite { get; }

    Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct);
}

/// What a tool hands back. A failed read is an error result the model can adapt to, not an
/// exception that ends the turn.
internal readonly record struct ToolInvocation(string Content, bool IsError)
{
    public static ToolInvocation Ok(string content) => new(content, false);

    public static ToolInvocation Error(string message) => new(message, true);

    public static implicit operator ToolInvocation(string content) => Ok(content);
}
