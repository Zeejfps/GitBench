using GitBench.Features.Assistant.Backend;

namespace GitBench.Features.Assistant;

/// <summary>
/// The model one role runs on: which provider, and which of its models. A null model is the
/// provider's own default.
/// </summary>
internal sealed record AssistantModelChoice(AssistantProvider Provider, string? Model)
{
    public static AssistantModelChoice Default { get; } = new(AssistantProviders.Default, null);

    /// <summary>A choice for a provider by id — one from a preferences file or a menu — with the model
    /// name trimmed. An id this build does not know resolves to the default provider.</summary>
    public static AssistantModelChoice For(string? providerId, string? model = null) =>
        new(AssistantProviders.Resolve(providerId), Trimmed(model));

    internal static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Where the assistant talks to: the model chosen for each role, and the endpoint chosen for each
/// provider that has been given one. The roles are independent — the chat, the review and the
/// walkthrough may each run on a different provider — and an endpoint belongs to its provider
/// whichever roles use it.
/// </summary>
internal sealed record AssistantSettings
{
    private readonly IReadOnlyDictionary<AssistantRole, AssistantModelChoice> _models;
    private readonly IReadOnlyDictionary<string, string> _baseUrls;

    private AssistantSettings(
        IReadOnlyDictionary<AssistantRole, AssistantModelChoice> models,
        IReadOnlyDictionary<string, string> baseUrls)
    {
        _models = models;
        _baseUrls = baseUrls;
    }

    /// <summary>Every role on the default provider's default model, every endpoint the provider's own.</summary>
    public static AssistantSettings Default { get; } = new(
        new Dictionary<AssistantRole, AssistantModelChoice>(),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Every role on one provider and model — what a single choice meant before roles had
    /// their own.</summary>
    public static AssistantSettings For(string? providerId, string? model = null, string? baseUrl = null)
    {
        var settings = Default.WithBaseUrl(providerId, baseUrl);
        foreach (var role in AssistantRoles.All)
            settings = settings.WithModel(role, providerId, model);
        return settings;
    }

    /// <summary>Rebuilds what was persisted. Entries for roles or providers this build no longer
    /// knows are dropped; a model on an unknown provider is dropped with it rather than silently
    /// moved to the default provider.</summary>
    public static AssistantSettings From(
        IEnumerable<(string Role, string ProviderId, string? Model)> models,
        IEnumerable<(string ProviderId, string? BaseUrl)> baseUrls)
    {
        var settings = Default;
        foreach (var (id, baseUrl) in baseUrls)
            if (AssistantProviders.Find(id) is { } provider)
                settings = settings.WithBaseUrl(provider.Id, baseUrl);
        foreach (var (roleId, providerId, model) in models)
            if (AssistantRoles.Parse(roleId) is { } role && AssistantProviders.Find(providerId) is { } provider)
                settings = settings.WithModel(role, provider.Id, model);
        return settings;
    }

    /// <summary>What a role runs on.</summary>
    public AssistantModelChoice ModelFor(AssistantRole role) =>
        _models.GetValueOrDefault(role, AssistantModelChoice.Default);

    /// <summary>The model that actually answers a role: the one chosen for it, or its provider's
    /// default for that role.</summary>
    public string EffectiveModelFor(AssistantRole role)
    {
        var choice = ModelFor(role);
        return choice.Model ?? choice.Provider.DefaultModelFor(role);
    }

    /// <summary>A provider's endpoint override, or null for its own.</summary>
    public string? BaseUrlFor(AssistantProvider provider) => _baseUrls.GetValueOrDefault(provider.Id);

    /// <summary>Every role's choice, for persistence.</summary>
    public IEnumerable<(AssistantRole Role, AssistantModelChoice Choice)> Models =>
        AssistantRoles.All.Select(role => (role, ModelFor(role)));

    /// <summary>Every endpoint override, keyed by provider id, for persistence.</summary>
    public IReadOnlyDictionary<string, string> BaseUrls => _baseUrls;

    /// <summary>The providers some role runs on.</summary>
    public IEnumerable<AssistantProvider> ProvidersInUse =>
        AssistantRoles.All.Select(role => ModelFor(role).Provider).DistinctBy(p => p.Id);

    public AssistantSettings WithModel(AssistantRole role, string? providerId, string? model) =>
        WithModel(role, AssistantModelChoice.For(providerId, model));

    public AssistantSettings WithModel(AssistantRole role, AssistantModelChoice choice)
    {
        if (choice == ModelFor(role)) return this;
        var next = new Dictionary<AssistantRole, AssistantModelChoice>(_models) { [role] = choice };
        return new AssistantSettings(next, _baseUrls);
    }

    /// <summary>Records a provider's endpoint; blank is the provider's own, which is what an absent
    /// entry already means.</summary>
    public AssistantSettings WithBaseUrl(string? providerId, string? baseUrl)
    {
        var provider = AssistantProviders.Resolve(providerId);
        var trimmed = AssistantModelChoice.Trimmed(baseUrl);
        if (string.Equals(trimmed, BaseUrlFor(provider), StringComparison.Ordinal)) return this;

        var next = new Dictionary<string, string>(_baseUrls, StringComparer.Ordinal);
        if (trimmed is null) next.Remove(provider.Id);
        else next[provider.Id] = trimmed;
        return new AssistantSettings(_models, next);
    }

    /// <summary>The connection a role's turns go out on, signed with the key its provider has.</summary>
    public AssistantConnection Connect(AssistantRole role, AssistantKeyring keys)
    {
        var choice = ModelFor(role);
        return AssistantConnection.For(
            choice.Provider, EffectiveModelFor(role), BaseUrlFor(choice.Provider), keys.For(choice.Provider).ApiKey);
    }

    public bool Equals(AssistantSettings? other) =>
        other is not null
        && AssistantRoles.All.All(role => ModelFor(role) == other.ModelFor(role))
        && _baseUrls.Count == other._baseUrls.Count
        && _baseUrls.All(kv => string.Equals(other._baseUrls.GetValueOrDefault(kv.Key), kv.Value, StringComparison.Ordinal));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var role in AssistantRoles.All) hash.Add(ModelFor(role));
        hash.Add(_baseUrls.Count);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Every role's connection at once, built from one settings and one keyring and replaced whole, so
/// the key that signs a role's request is by construction the key of the provider that role points
/// at.
/// </summary>
internal sealed class AssistantConnections
{
    private readonly IReadOnlyDictionary<AssistantRole, AssistantConnection> _byRole;

    private AssistantConnections(IReadOnlyDictionary<AssistantRole, AssistantConnection> byRole) => _byRole = byRole;

    public static AssistantConnections Build(AssistantSettings settings, AssistantKeyring keys)
    {
        var byRole = new Dictionary<AssistantRole, AssistantConnection>();
        foreach (var role in AssistantRoles.All)
            byRole[role] = settings.Connect(role, keys);
        return new AssistantConnections(byRole);
    }

    public AssistantConnection For(AssistantRole role) => _byRole[role];
}
