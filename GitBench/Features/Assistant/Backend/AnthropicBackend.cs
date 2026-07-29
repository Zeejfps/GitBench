using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Talks to the Messages API over <see cref="HttpClient"/> and turns its event stream into
/// <see cref="BackendEvent"/>s.
/// </summary>
/// <remarks>
/// Hand-rolled rather than SDK-backed: the surface needed is small, and a generated dependency has
/// unverified NativeAOT behaviour in this app.
/// </remarks>
internal sealed class AnthropicBackend : IAssistantBackend
{
    private const string Path = "/messages";
    private const string ApiVersion = "2023-06-01";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const string DataPrefix = "data:";

    private readonly HttpClient _http;
    private readonly Func<AssistantConnection> _connection;

    // The connection is read per request rather than captured, so a key or model changed after
    // startup takes effect without rebuilding the backend.
    public AnthropicBackend(HttpClient http, Func<AssistantConnection> connection)
    {
        _http = http;
        _connection = connection;
    }

    public async IAsyncEnumerable<BackendEvent> SendAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var connection = _connection();
        if (connection.ApiKey is not { } key)
        {
            yield return new BackendEvent.Error($"No {connection.Provider.DisplayName} API key is configured.");
            yield break;
        }

        var (response, sendFailure) = await SendRequestAsync(turn, tools, connection, key, ct).ConfigureAwait(false);
        if (response is null)
        {
            yield return new BackendEvent.Error(sendFailure ?? "The request could not be sent.");
            yield break;
        }

        using var owned = response;
        if (!owned.IsSuccessStatusCode)
        {
            yield return await AssistantHttpError.ReadAsync(owned, ct).ConfigureAwait(false);
            yield break;
        }

        await using var stream = await owned.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await foreach (var backendEvent in ReadEventsAsync(reader, ct).ConfigureAwait(false))
            yield return backendEvent;
    }

    private async Task<(HttpResponseMessage? Response, string? Failure)> SendRequestAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        AssistantConnection connection,
        string key,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint(Path));
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (connection.Provider.SupportsServerSideFallbacks(turn.Tier))
            request.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
        request.Content = new ByteArrayContent(AnthropicRequestWriter.Write(turn, tools, connection));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static async IAsyncEnumerable<BackendEvent> ReadEventsAsync(
        StreamReader reader,
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
