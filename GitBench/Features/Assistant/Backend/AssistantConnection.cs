namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Everything one request needs to reach a model: whose wire format it speaks, where it posts, the
/// key that signs it, and the model that answers. One per role — the chat, the review and the
/// walkthrough each have their own, and may point at different providers.
/// </summary>
internal sealed record AssistantConnection(
    AssistantProvider Provider,
    string BaseUrl,
    string? ApiKey,
    string Model)
{
    public static AssistantConnection For(
        AssistantProvider provider,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null) =>
        new(
            provider,
            Trimmed(baseUrl) ?? provider.BaseUrl,
            Trimmed(apiKey),
            Trimmed(model) ?? provider.DefaultModel);

    public static AssistantConnection Default { get; } = For(AssistantProviders.Default);

    /// <summary>Whether a turn can be attempted at all: a key resolved, or a provider that needs none.</summary>
    public bool IsUsable => !Provider.RequiresApiKey || ApiKey is not null;

    /// <summary>What the model answering this connection accepts.</summary>
    public AssistantModel Capabilities => Provider.Capabilities(Model);

    /// <summary>The turn's own cap, held under whatever the provider will actually accept.</summary>
    public int MaxTokensFor(AssistantTurn turn) => Math.Min(turn.MaxTokens, Provider.MaxOutputTokens);

    public string Endpoint(string path) => BaseUrl.TrimEnd('/') + path;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
