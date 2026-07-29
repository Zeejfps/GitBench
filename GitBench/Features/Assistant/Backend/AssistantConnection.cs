namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Everything one request needs to reach a model: whose wire format it speaks, where it posts, the
/// key that signs it, and the model each tier runs on.
/// </summary>
/// <remarks>
/// A user-set model applies to both tiers: a self-hosted endpoint usually has exactly one model
/// loaded, so falling back to a provider default for quick actions would ask for one that isn't there.
/// </remarks>
internal sealed record AssistantConnection(
    AssistantProvider Provider,
    string BaseUrl,
    string? ApiKey,
    string ChatModel,
    string QuickModel)
{
    public static AssistantConnection For(
        AssistantProvider provider,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null)
    {
        var chosen = Trimmed(model);
        return new AssistantConnection(
            provider,
            Trimmed(baseUrl) ?? provider.BaseUrl,
            Trimmed(apiKey),
            chosen ?? provider.ChatModel,
            chosen ?? provider.QuickModel);
    }

    public static AssistantConnection Default { get; } = For(AssistantProviders.Default);

    /// <summary>Whether a turn can be attempted at all: a key resolved, or a provider that needs none.</summary>
    public bool IsUsable => !Provider.RequiresApiKey || ApiKey is not null;

    public string ModelFor(ModelTier tier) => tier == ModelTier.Quick ? QuickModel : ChatModel;

    /// <summary>The turn's own cap, held under whatever the provider will actually accept.</summary>
    public int MaxTokensFor(AssistantTurn turn) => Math.Min(turn.MaxTokens, Provider.MaxOutputTokens);

    public string Endpoint(string path) => BaseUrl.TrimEnd('/') + path;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
