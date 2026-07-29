using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Turns a <c>/v1/chat/completions</c> event stream into <see cref="BackendEvent"/>s.
/// </summary>
/// <remarks>
/// Split from the backend so the framing can be exercised without a socket. Tool calls arrive in
/// pieces keyed by position and their arguments are a JSON-encoded string, so they are assembled
/// here and handed on as real JSON — nothing past this class knows which wire format produced them.
/// </remarks>
internal static class OpenAiStreamReader
{
    private const string DataPrefix = "data:";
    private const string Done = "[DONE]";

    public static async IAsyncEnumerable<BackendEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var calls = new SortedDictionary<int, PartialCall>();
        string? finish = null;
        var done = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                continue;

            var payload = line[DataPrefix.Length..].Trim();
            if (payload.Length == 0)
                continue;

            if (payload == Done)
            {
                done = true;
                break;
            }

            var chunk = TryParse(payload);
            if (chunk is null)
                continue;

            if (chunk.Error is { } error)
            {
                yield return new BackendEvent.Error(
                    error.Message ?? "The assistant stream reported an error.",
                    error.Type);
                yield break;
            }

            foreach (var choice in chunk.Choices ?? [])
            {
                if (choice.Delta?.Content is { Length: > 0 } text)
                    yield return new BackendEvent.TextDelta(text);

                // Reasoning models on this wire stream their thinking separately; the transcript
                // shows that it happened and never the content.
                if (choice.Delta?.ReasoningContent is { Length: > 0 })
                    yield return new BackendEvent.Thinking();

                foreach (var call in choice.Delta?.ToolCalls ?? [])
                    Accumulate(calls, call);

                if (choice.FinishReason is { Length: > 0 } reason)
                    finish = reason;
            }
        }

        // A content filter is settled from the finish reason rather than by reading what arrived,
        // which may be empty or partial.
        if (finish == "content_filter")
        {
            yield return new BackendEvent.Refusal("content_filter", null);
            yield break;
        }

        // A healthy stream carries both terminators, so either one alone is enough. Neither means the
        // body ended on its own — close-delimited endpoints make that indistinguishable from a
        // finished turn, and reading it as one commits a truncated answer to the conversation.
        if (!done && finish is null)
        {
            yield return new BackendEvent.Error("The assistant stream ended before the turn completed.");
            yield break;
        }

        foreach (var call in calls.Values)
            yield return new BackendEvent.ToolUse(call.Id, call.Name, ParseArguments(call.Arguments.ToString()));

        yield return new BackendEvent.TurnComplete(MapFinishReason(finish, calls.Count > 0));
    }

    private static void Accumulate(SortedDictionary<int, PartialCall> calls, OpenAiToolCallDelta delta)
    {
        var index = delta.Index ?? calls.Count;
        if (!calls.TryGetValue(index, out var call))
        {
            call = new PartialCall();
            calls[index] = call;
        }

        if (delta.Id is { Length: > 0 } id)
            call.Id = id;
        if (delta.Function?.Name is { Length: > 0 } name)
            call.Name = name;
        if (delta.Function?.Arguments is { Length: > 0 } arguments)
            call.Arguments.Append(arguments);
    }

    private static OpenAiStreamChunk? TryParse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, AssistantJsonContext.Default.OpenAiStreamChunk);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ParseArguments(string json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    private static JsonElement Empty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    // A stream that reached its sentinel without saying why but left tool calls behind is a tool-call
    // turn; the alternative would report an answer the model never gave.
    private static StopReason MapFinishReason(string? reason, bool hasToolCalls) => reason switch
    {
        "stop" => StopReason.EndTurn,
        "tool_calls" => StopReason.ToolUse,
        "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        null when hasToolCalls => StopReason.ToolUse,
        null => StopReason.EndTurn,
        _ => StopReason.Other,
    };

    private sealed class PartialCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
