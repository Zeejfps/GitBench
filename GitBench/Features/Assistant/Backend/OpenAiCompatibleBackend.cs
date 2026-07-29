using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Talks to any endpoint that speaks <c>/v1/chat/completions</c> — OpenAI, Ollama, LM Studio,
/// OpenRouter, Groq, Together, vLLM — and turns its event stream into <see cref="BackendEvent"/>s.
/// </summary>
/// <remarks>
/// A sibling of <see cref="AnthropicBackend"/> rather than a variation on it: the two share the
/// event vocabulary and nothing else, so what they have in common is the seam, not a base class.
/// </remarks>
internal sealed class OpenAiCompatibleBackend : IAssistantBackend
{
    private const string Path = "/chat/completions";

    private readonly HttpClient _http;
    private readonly Func<AssistantConnection> _connection;

    // The connection is read per request rather than captured, so a key, model or endpoint changed
    // after startup takes effect without rebuilding the backend.
    public OpenAiCompatibleBackend(HttpClient http, Func<AssistantConnection> connection)
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
        if (!connection.IsUsable)
        {
            yield return new BackendEvent.Error($"No {connection.Provider.DisplayName} API key is configured.");
            yield break;
        }

        var (response, sendFailure) = await SendRequestAsync(turn, tools, connection, ct).ConfigureAwait(false);
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
        await foreach (var backendEvent in OpenAiStreamReader.ReadAsync(reader, ct).ConfigureAwait(false))
            yield return backendEvent;
    }

    private async Task<(HttpResponseMessage? Response, string? Failure)> SendRequestAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        AssistantConnection connection,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint(Path));
        // A local endpoint needs no key, but one given for it is still sent: self-hosted gateways
        // are routinely put behind a token.
        if (connection.ApiKey is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new ByteArrayContent(OpenAiRequestWriter.Write(turn, tools, connection));
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
}
