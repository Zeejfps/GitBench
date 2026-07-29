using System.Text.Json.Serialization;

namespace GitBench.Features.Assistant.Backend;

// The wire shapes the backend reads back: one SSE frame, and the error envelope a non-2xx
// response carries. Requests are written directly with Utf8JsonWriter so the tool list and system
// block are byte-stable for the prompt cache.
internal sealed class AnthropicStreamEvent
{
    public string? Type { get; set; }
    public int? Index { get; set; }
    public AnthropicStreamBlock? ContentBlock { get; set; }
    public AnthropicStreamDelta? Delta { get; set; }
    public AssistantError? Error { get; set; }
}

internal sealed class AnthropicStreamBlock
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class AnthropicStreamDelta
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? PartialJson { get; set; }
    public string? StopReason { get; set; }
    public AnthropicStopDetails? StopDetails { get; set; }
}

internal sealed class AnthropicStopDetails
{
    public string? Category { get; set; }
    public string? Explanation { get; set; }
}

// Both wires report a failure the same way, so one shape reads either.
internal sealed class AssistantError
{
    public string? Type { get; set; }
    public string? Message { get; set; }
}

internal sealed class AssistantErrorEnvelope
{
    public AssistantError? Error { get; set; }
}

// The OpenAI-compatible wire's read side. A tool call arrives in pieces keyed by position, and its
// arguments are a JSON-encoded string rather than an object.
internal sealed class OpenAiStreamChunk
{
    public List<OpenAiStreamChoice>? Choices { get; set; }
    public AssistantError? Error { get; set; }
}

internal sealed class OpenAiStreamChoice
{
    public OpenAiStreamDelta? Delta { get; set; }
    public string? FinishReason { get; set; }
}

internal sealed class OpenAiStreamDelta
{
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<OpenAiToolCallDelta>? ToolCalls { get; set; }
}

internal sealed class OpenAiToolCallDelta
{
    public int? Index { get; set; }
    public string? Id { get; set; }
    public OpenAiFunctionDelta? Function { get; set; }
}

internal sealed class OpenAiFunctionDelta
{
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(AssistantErrorEnvelope))]
internal partial class AssistantJsonContext : JsonSerializerContext;
