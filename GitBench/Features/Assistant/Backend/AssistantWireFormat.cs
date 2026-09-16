using System.Net.Http.Headers;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Which request shape a provider speaks: the Messages API, or the <c>/v1/chat/completions</c> shape
/// OpenAI, Ollama, LM Studio, OpenRouter, Groq, Together and vLLM all share. Everything that differs
/// between the two on the wire lives here; the send shell is shared.
/// </summary>
internal sealed record AssistantWireFormat(
    string Path,
    Action<HttpRequestHeaders, AssistantConnection, AssistantTurn> Headers,
    Func<AssistantTurn, IReadOnlyList<IAssistantTool>, AssistantConnection, byte[]> Write,
    Func<TextReader, CancellationToken, IAsyncEnumerable<BackendEvent>> Read)
{
    public static AssistantWireFormat Anthropic { get; } = new(
        "/messages",
        AnthropicHeaders,
        AnthropicRequestWriter.Write,
        AnthropicStreamReader.ReadAsync);

    public static AssistantWireFormat OpenAiCompatible { get; } = new(
        "/chat/completions",
        OpenAiHeaders,
        OpenAiRequestWriter.Write,
        OpenAiStreamReader.ReadAsync);

    private static void AnthropicHeaders(HttpRequestHeaders headers, AssistantConnection connection, AssistantTurn turn)
    {
        if (connection.ApiKey is { } key)
            headers.TryAddWithoutValidation("x-api-key", key);
        headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (connection.Capabilities.ServerSideFallbacks)
            headers.TryAddWithoutValidation("anthropic-beta", "server-side-fallback-2026-07-01");
    }

    // A local endpoint needs no key, but one given for it is still sent: self-hosted gateways
    // are routinely put behind a token.
    private static void OpenAiHeaders(HttpRequestHeaders headers, AssistantConnection connection, AssistantTurn turn)
    {
        if (connection.ApiKey is { } key)
            headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
