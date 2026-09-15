using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Posts each turn over <see cref="HttpClient"/> in whichever wire format the connection's provider
/// speaks and turns the event stream into <see cref="BackendEvent"/>s.
/// </summary>
/// <remarks>
/// Hand-rolled rather than SDK-backed: the surface needed is small, and a generated dependency has
/// unverified NativeAOT behaviour in this app. The connection is read per request rather than
/// captured, so a provider, key, model or endpoint changed after startup takes effect on the next
/// message without rebuilding the backend.
/// </remarks>
internal sealed class HttpAssistantBackend : IAssistantBackend
{
    private readonly HttpClient _http;
    private readonly Func<AssistantConnection> _connection;

    public HttpAssistantBackend(HttpClient http, Func<AssistantConnection> connection)
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

        var wire = connection.Provider.Wire;
        var (response, sendFailure) = await SendRequestAsync(wire, turn, tools, connection, ct).ConfigureAwait(false);
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
        await foreach (var backendEvent in wire.Read(reader, ct).ConfigureAwait(false))
            yield return backendEvent;
    }

    private async Task<(HttpResponseMessage? Response, string? Failure)> SendRequestAsync(
        AssistantWireFormat wire,
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        AssistantConnection connection,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint(wire.Path));
            wire.Headers(request.Headers, connection, turn);
            request.Content = new ByteArrayContent(wire.Write(turn, tools, connection));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or UriFormatException or NotSupportedException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }
}
