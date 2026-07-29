using GitBench.Features.Assistant.Tools;

namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Sends each turn to whichever provider is configured at the moment it starts, so changing provider
/// takes effect on the next message instead of rebuilding every conversation behind it.
/// </summary>
internal sealed class AssistantBackendRouter : IAssistantBackend
{
    private readonly Func<AssistantConnection> _connection;
    private readonly IAssistantBackend _anthropic;
    private readonly IAssistantBackend _openAi;

    public AssistantBackendRouter(HttpClient http, Func<AssistantConnection> connection)
    {
        _connection = connection;
        _anthropic = new AnthropicBackend(http, connection);
        _openAi = new OpenAiCompatibleBackend(http, connection);
    }

    public IAsyncEnumerable<BackendEvent> SendAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        CancellationToken ct)
    {
        var backend = _connection().Provider.Wire == AssistantWireFormat.Anthropic ? _anthropic : _openAi;
        return backend.SendAsync(turn, tools, ct);
    }
}
