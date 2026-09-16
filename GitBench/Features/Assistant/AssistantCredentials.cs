using System.Diagnostics;
using GitBench.Features.Assistant.Backend;
using ZGF.Gui.Desktop;

namespace GitBench.Features.Assistant;

/// Where the key in effect came from. A provider that needs none is configured without one, which is
/// a resting state rather than a gap.
internal enum AssistantKeySource
{
    None,
    Saved,
    Environment,
    NotRequired,
}

/// <summary>
/// What is known about one provider's key: the key saved for it, the one its environment variable
/// offers, and whether it needs one at all. Both keys are carried because they are not the app's in
/// the same way — the saved one it owns and may hand back, the environment's it only reads.
/// </summary>
internal readonly record struct AssistantKeyState(string? SavedKey, string? EnvironmentKey, bool RequiresKey)
{
    /// <summary>The key that would sign a request, or null when there is none to sign with.</summary>
    public string? ApiKey => SavedKey ?? EnvironmentKey;

    public AssistantKeySource Source =>
        SavedKey is not null ? AssistantKeySource.Saved
        : EnvironmentKey is not null ? AssistantKeySource.Environment
        : RequiresKey ? AssistantKeySource.None : AssistantKeySource.NotRequired;

    /// <summary>Whether a turn can be attempted on this provider at all.</summary>
    public bool IsUsable => !RequiresKey || ApiKey is not null;
}

/// <summary>
/// What is known about every provider's key at once.
/// </summary>
/// <remarks>
/// One value rather than a source and a secret that arrive separately: a reader asks for a provider
/// and gets that provider's key, so no window and no ordering of updates can pair one provider's id
/// with another's secret. Every path that fills a field or signs a request goes through here.
/// </remarks>
internal sealed class AssistantKeyring
{
    private readonly IReadOnlyDictionary<string, AssistantKeyState> _states;

    public AssistantKeyring(IReadOnlyDictionary<string, AssistantKeyState> states) => _states = states;

    /// <summary>Nothing known yet — every provider reads as having no key.</summary>
    public static AssistantKeyring Empty { get; } =
        new(new Dictionary<string, AssistantKeyState>(StringComparer.Ordinal));

    public AssistantKeyState For(AssistantProvider provider) =>
        _states.TryGetValue(provider.Id, out var state)
            ? state
            : new AssistantKeyState(null, null, provider.RequiresApiKey);

    /// <summary>The same keyring with one provider's state replaced.</summary>
    public AssistantKeyring With(AssistantProvider provider, AssistantKeyState state)
    {
        var next = new Dictionary<string, AssistantKeyState>(_states, StringComparer.Ordinal)
        {
            [provider.Id] = state,
        };
        return new AssistantKeyring(next);
    }
}

/// <summary>
/// What a save asks of the stored keys: nothing, or one provider's key written or forgotten. A key
/// is only ever edited for the provider it was typed or read for, which is what carrying the
/// provider inside the edit enforces.
/// </summary>
internal abstract record AssistantKeyEdit
{
    private AssistantKeyEdit() { }

    public static AssistantKeyEdit None { get; } = new Keep();

    public sealed record Keep : AssistantKeyEdit;

    public sealed record Forget(AssistantProvider Provider) : AssistantKeyEdit;

    public sealed record Store : AssistantKeyEdit
    {
        public Store(AssistantProvider provider, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A stored key cannot be blank.", nameof(key));
            Provider = provider;
            Key = key.Trim();
        }

        public AssistantProvider Provider { get; }

        public string Key { get; }
    }

    /// <summary>The keyring as it will read once this edit has landed, minus whatever only the
    /// secret store can add: the answer a resolve comes back with, available before it does.</summary>
    public AssistantKeyring ApplyTo(AssistantKeyring keys) => this switch
    {
        Keep => keys,
        Forget forget => keys.With(forget.Provider, keys.For(forget.Provider) with { SavedKey = null }),
        Store store => keys.With(store.Provider, keys.For(store.Provider) with { SavedKey = store.Key }),
        _ => throw new UnreachableException(),
    };
}

/// <summary>
/// Answers where a provider's API key comes from: the key saved for it in the OS secret store, else
/// the provider's environment variable, else none. Only the secret store is written — an environment
/// variable is a fallback the app reads and never owns, so clearing a saved key can still leave a key
/// in effect. A provider that needs no key at all reports as configured without one.
/// </summary>
internal sealed class AssistantCredentials
{
    private readonly ISecretStore _secrets;

    public AssistantCredentials(ISecretStore secrets)
    {
        _secrets = secrets;
    }

    public string? ApiKeyFor(AssistantProvider provider) => SavedFor(provider) ?? FromEnvironment(provider);

    public string? SavedFor(AssistantProvider provider) => Normalize(_secrets.Get(provider.SecretName));

    public string? FromEnvironment(AssistantProvider provider) => provider.Hosting switch
    {
        AssistantHosting.Hosted hosted => Normalize(Environment.GetEnvironmentVariable(hosted.EnvironmentVariable)),
        AssistantHosting.SelfHosted => null,
        _ => throw new UnreachableException(),
    };

    public AssistantKeySource SourceFor(AssistantProvider provider)
    {
        if (SavedFor(provider) is not null) return AssistantKeySource.Saved;
        if (FromEnvironment(provider) is not null) return AssistantKeySource.Environment;
        return provider.RequiresApiKey ? AssistantKeySource.None : AssistantKeySource.NotRequired;
    }

    /// <summary>Reads what every provider has in one pass, so a card or a switcher can answer for a
    /// provider other than the one in use without a second trip. Blocks on the OS store once per
    /// provider, which is why it belongs on a worker.</summary>
    public AssistantKeyring Keyring()
    {
        var states = new Dictionary<string, AssistantKeyState>(StringComparer.Ordinal);
        foreach (var provider in AssistantProviders.All)
            states[provider.Id] = StateFor(provider);
        return new AssistantKeyring(states);
    }

    public AssistantKeyState StateFor(AssistantProvider provider) =>
        new(SavedFor(provider), FromEnvironment(provider), provider.RequiresApiKey);

    public bool Save(AssistantProvider provider, string apiKey)
    {
        var key = Normalize(apiKey);
        return key != null && _secrets.Set(provider.SecretName, key);
    }

    public bool Clear(AssistantProvider provider) => _secrets.Delete(provider.SecretName);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
