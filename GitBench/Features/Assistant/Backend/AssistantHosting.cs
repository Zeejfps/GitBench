namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Whether a provider is a product with a published catalogue and a key convention, or an endpoint
/// the user runs and points the app at. Everything that follows from that — a key being required,
/// the address being editable, a model list being worth offering, tool calling being proven —
/// follows from which case this is.
/// </summary>
internal abstract record AssistantHosting
{
    private AssistantHosting() { }

    /// <summary>A hosted product: needs a key, read from <paramref name="EnvironmentVariable"/> when
    /// none is saved, and serves the listed models in the order the picker offers them.</summary>
    public sealed record Hosted(string EnvironmentVariable, IReadOnlyList<AssistantModel> Models) : AssistantHosting;

    /// <summary>A local endpoint: answers unauthenticated, its address is the user's to set, it
    /// serves whatever the user loaded, and whether that model calls tools is unknown.</summary>
    public sealed record SelfHosted : AssistantHosting;
}
