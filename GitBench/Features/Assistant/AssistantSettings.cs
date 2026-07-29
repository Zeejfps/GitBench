using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant;

/// <summary>
/// The model and endpoint chosen for one provider. Null on either means the provider's own default.
/// </summary>
internal sealed record AssistantProviderChoice(string? Model, string? BaseUrl)
{
    public static AssistantProviderChoice None { get; } = new(null, null);

    public bool IsEmpty => Model is null && BaseUrl is null;
}

/// <summary>
/// Where the assistant talks to: which provider is selected, and the model and endpoint chosen for
/// each provider that has ever been given one. An override belongs to its own provider — selecting
/// another restores what was last used there rather than carrying a model name that means nothing
/// to it.
/// </summary>
internal sealed record AssistantSettings
{
    private readonly IReadOnlyDictionary<string, AssistantProviderChoice> _choices;

    private AssistantSettings(string providerId, IReadOnlyDictionary<string, AssistantProviderChoice> choices)
    {
        ProviderId = providerId;
        _choices = choices;
    }

    public static AssistantSettings Default { get; } = new(
        AssistantProviders.Default.Id,
        new Dictionary<string, AssistantProviderChoice>(StringComparer.Ordinal));

    /// <summary>One provider selected and given its overrides, every other at its own defaults.</summary>
    public static AssistantSettings For(string? providerId, string? model = null, string? baseUrl = null) =>
        Default.With(providerId, model, baseUrl);

    /// <summary>Rebuilds what was persisted: the provider last selected, and what each provider was
    /// last given. Entries for providers this build no longer knows are dropped.</summary>
    public static AssistantSettings From(
        string? providerId,
        IEnumerable<(string ProviderId, string? Model, string? BaseUrl)> choices)
    {
        var settings = Default.Select(providerId);
        foreach (var (id, model, baseUrl) in choices)
            if (AssistantProviders.Find(id) is { } provider)
                settings = settings.Remember(provider.Id, model, baseUrl);
        return settings;
    }

    public string ProviderId { get; }

    public AssistantProvider Provider => AssistantProviders.Resolve(ProviderId);

    /// <summary>The selected provider's model override, or null for its own.</summary>
    public string? Model => ChoiceFor(ProviderId).Model;

    /// <summary>The selected provider's endpoint override, or null for its own.</summary>
    public string? BaseUrl => ChoiceFor(ProviderId).BaseUrl;

    /// <summary>What a provider was last given, whether or not it is the selected one.</summary>
    public AssistantProviderChoice ChoiceFor(string? providerId) =>
        _choices.GetValueOrDefault(AssistantProviders.Resolve(providerId).Id, AssistantProviderChoice.None);

    /// <summary>Every remembered choice, keyed by provider id, for persistence.</summary>
    public IReadOnlyDictionary<string, AssistantProviderChoice> Choices => _choices;

    /// <summary>Selects a provider, restoring the model and endpoint last used with it.</summary>
    public AssistantSettings Select(string? providerId)
    {
        var id = AssistantProviders.Resolve(providerId).Id;
        return string.Equals(id, ProviderId, StringComparison.Ordinal)
            ? this
            : new AssistantSettings(id, _choices);
    }

    /// <summary>Selects a provider and records the model and endpoint chosen for it, keeping what
    /// every other provider was given.</summary>
    public AssistantSettings With(string? providerId, string? model, string? baseUrl) =>
        Select(providerId).Remember(providerId, model, baseUrl);

    public AssistantConnection Connect(string? apiKey) =>
        AssistantConnection.For(Provider, Model, BaseUrl, apiKey);

    private AssistantSettings Remember(string? providerId, string? model, string? baseUrl)
    {
        var id = AssistantProviders.Resolve(providerId).Id;
        var choice = new AssistantProviderChoice(Trimmed(model), Trimmed(baseUrl));
        if (choice == ChoiceFor(id)) return this;

        var next = new Dictionary<string, AssistantProviderChoice>(_choices, StringComparer.Ordinal);
        // An empty choice is the provider's own defaults, which is what an absent entry already means.
        if (choice.IsEmpty) next.Remove(id);
        else next[id] = choice;
        return new AssistantSettings(ProviderId, next);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
