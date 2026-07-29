using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The session store as the view model sees it, with the secret store replaced by a per-provider
/// map. Saves are recorded as the pair that matters — which provider, and what the save asked of its
/// key — so a test can assert not only what was written but whose slot it was written to.
/// </summary>
internal sealed class FakeAssistantSessionStore : IAssistantSessionStore, IDisposable
{
    private readonly State<AssistantSession?> _active = new(null);
    private readonly State<CommitMessageQuickAction?> _commitMessage = new(null);
    private readonly State<AssistantSettings> _settings = new(AssistantSettings.Default);
    private readonly State<bool> _configured = new(false);
    private readonly State<AssistantKeyring> _keys = new(AssistantKeyring.Empty);
    private readonly Dictionary<string, AssistantKeyState> _states = new(StringComparer.Ordinal);

    public IReadable<AssistantSession?> Active => _active;
    public IReadable<CommitMessageQuickAction?> CommitMessage => _commitMessage;
    public IReadable<AssistantSettings> Settings => _settings;
    public IReadable<bool> IsConfigured => _configured;
    public IReadable<AssistantKeyring> Keys => _keys;

    public AssistantSettings? Saved { get; private set; }

    /// <summary>What the last save asked of the key: null to leave it, empty to forget it.</summary>
    public string? SavedApiKey { get; private set; }

    /// <summary>Every save, in order, as the provider it was for and the key edit it carried.</summary>
    public List<(string ProviderId, string? ApiKey)> Writes { get; } = new();

    public void Save(AssistantSettings settings, string? apiKey)
    {
        Saved = settings;
        SavedApiKey = apiKey;
        Writes.Add((settings.ProviderId, apiKey));
        _settings.Value = settings;

        // The real store writes the edit into the saved provider's own slot and nowhere else.
        if (apiKey is not null)
            SetSavedKey(settings.Provider, apiKey.Length == 0 ? null : apiKey);
        else
            Publish();
    }

    /// <summary>The preset runs this store was asked for, so a test can tell "the view model
    /// asked" from "a turn actually started".</summary>
    public List<(string Agent, string Prompt)> Presets { get; } = new();

    public void RunPreset(string agentName, string prompt) => Presets.Add((agentName, prompt));

    public void SetConfigured(bool configured) => _configured.Value = configured;

    /// <summary>Gives a provider a key the app itself saved — the one the card may hold.</summary>
    public void SetSavedKey(AssistantProvider provider, string? key)
    {
        var state = StateFor(provider);
        Set(provider, state with { SavedKey = key });
    }

    /// <summary>Gives a provider a key it inherits from the environment, which the app reads and
    /// never owns.</summary>
    public void SetEnvironmentKey(AssistantProvider provider, string? key)
    {
        var state = StateFor(provider);
        Set(provider, state with { EnvironmentKey = key });
    }

    /// <summary>What a provider is holding, as the view model would read it.</summary>
    public AssistantKeyState KeyStateFor(AssistantProvider provider) => _keys.Value.For(provider);

    private AssistantKeyState StateFor(AssistantProvider provider) =>
        _states.TryGetValue(provider.Id, out var state)
            ? state
            : new AssistantKeyState(null, null, provider.RequiresApiKey);

    private void Set(AssistantProvider provider, AssistantKeyState state)
    {
        _states[provider.Id] = state;
        Publish();
    }

    private void Publish()
    {
        _keys.Value = new AssistantKeyring(new Dictionary<string, AssistantKeyState>(_states, StringComparer.Ordinal));
        _configured.Value = _keys.Value.For(_settings.Value.Provider).IsUsable;
    }

    public void Dispose()
    {
        _active.Dispose();
        _commitMessage.Dispose();
        _settings.Dispose();
        _configured.Dispose();
        _keys.Dispose();
    }
}
