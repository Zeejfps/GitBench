using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Turns a Messages API event stream into <see cref="BackendEvent"/>s.
/// </summary>
internal static class AnthropicStreamReader
{
    private const string DataPrefix = "data:";

    public static async IAsyncEnumerable<BackendEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var toolIds = new Dictionary<int, string>();
        var toolNames = new Dictionary<int, string>();
        var toolInput = new Dictionary<int, StringBuilder>();
        var stop = StopReason.EndTurn;
        var terminated = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                continue;

            var payload = line[DataPrefix.Length..].Trim();
            if (payload.Length == 0)
                continue;

            var frame = TryParseFrame(payload);
            if (frame?.Type is null)
                continue;

            switch (frame.Type)
            {
                case "content_block_start":
                    if (frame.ContentBlock?.Type == "tool_use" && frame.Index is { } startIndex)
                    {
                        toolIds[startIndex] = frame.ContentBlock.Id ?? string.Empty;
                        toolNames[startIndex] = frame.ContentBlock.Name ?? string.Empty;
                        toolInput[startIndex] = new StringBuilder();
                    }
                    else if (frame.ContentBlock?.Type == "thinking")
                    {
                        yield return new BackendEvent.Thinking();
                    }

                    break;

                case "content_block_delta":
                    if (frame.Delta?.Type == "text_delta" && frame.Delta.Text is { Length: > 0 } text)
                        yield return new BackendEvent.TextDelta(text);
                    else if (frame.Delta?.Type == "input_json_delta" && frame.Index is { } deltaIndex
                             && toolInput.TryGetValue(deltaIndex, out var buffer))
                        buffer.Append(frame.Delta.PartialJson);
                    break;

                case "content_block_stop":
                    if (frame.Index is { } stopIndex && toolInput.Remove(stopIndex, out var completed))
                    {
                        var input = ParseToolInput(completed.ToString());
                        yield return new BackendEvent.ToolUse(
                            toolIds.GetValueOrDefault(stopIndex, string.Empty),
                            toolNames.GetValueOrDefault(stopIndex, string.Empty),
                            input);
                    }

                    break;

                case "message_delta":
                    // A refusal returns HTTP 200 with empty or partial content, so it is settled
                    // from the stop reason rather than by reading what arrived.
                    if (frame.Delta?.StopReason == "refusal")
                    {
                        terminated = true;
                        yield return new BackendEvent.Refusal(
                            frame.Delta.StopDetails?.Category,
                            frame.Delta.StopDetails?.Explanation);
                        yield break;
                    }

                    if (frame.Delta?.StopReason is { } reason)
                        stop = MapStopReason(reason);
                    break;

                case "message_stop":
                    terminated = true;
                    yield return new BackendEvent.TurnComplete(stop);
                    yield break;

                case "error":
                    terminated = true;
                    yield return new BackendEvent.Error(
                        frame.Error?.Message ?? "The assistant stream reported an error.",
                        frame.Error?.Type);
                    yield break;
            }
        }

        if (!terminated)
            yield return new BackendEvent.Error("The assistant stream ended before the turn completed.");
    }

    private static AnthropicStreamEvent? TryParseFrame(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, AssistantJsonContext.Default.AnthropicStreamEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ParseToolInput(string json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private static StopReason MapStopReason(string reason) => reason switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "pause_turn" => StopReason.PauseTurn,
        _ => StopReason.Other,
    };
}
