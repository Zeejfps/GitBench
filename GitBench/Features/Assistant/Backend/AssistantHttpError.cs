using System.Text.Json;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Turns a non-2xx response from either wire into a <see cref="BackendEvent.Error"/>.
/// </summary>
internal static class AssistantHttpError
{
    public static async Task<BackendEvent> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fall through to the status-only message.
        }

        var message = TryParseMessage(body) ?? $"The assistant request failed ({(int)response.StatusCode}).";
        return new BackendEvent.Error(message, string.IsNullOrWhiteSpace(body) ? null : body);
    }

    private static string? TryParseMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize(body, AssistantJsonContext.Default.AssistantErrorEnvelope);
            return envelope?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
