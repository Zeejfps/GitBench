using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The session store as the view model sees it, with the secret store replaced by a per-provider
/// map. Saves are recorded as the key edit they carried — which provider, and what was asked of its
/// key — so a test can assert not only what was written but whose slot it was written to.
/// </summary>
internal sealed class FakeAssistantSessionStore : IAssistantSessionStore, IDisposable
{
    private readonly State<AssistantSession?> _active = new(null);
    private readonly State<CommitMessageQuickAction?> _commitMessage = new(null);
    private readonly State<AssistantSettings> _settings = new(AssistantSettings.Default);
    private readonly Dictionary<AssistantRole, State<bool>> _configured =
        AssistantRoles.All.ToDictionary(role => role, _ => new State<bool>(false));
    private readonly State<AssistantKeyring> _keys = new(AssistantKeyring.Empty);
    private readonly Dictionary<string, AssistantKeyState> _states = new(StringComparer.Ordinal);

    public IReadable<AssistantSession?> Active => _active;
    public IReadable<CommitMessageQuickAction?> CommitMessage => _commitMessage;
    public IReadable<AssistantSettings> Settings => _settings;
    public IReadable<bool> IsConfigured(AssistantRole role) => _configured[role];
    public IReadable<AssistantKeyring> Keys => _keys;

    public AssistantSettings? Saved { get; private set; }

    /// <summary>What the last save asked of the keys.</summary>
    public AssistantKeyEdit? SavedKey { get; private set; }

    /// <summary>Every key edit saved, in order, skipping the saves that asked nothing of the keys.</summary>
    public List<AssistantKeyEdit> Writes { get; } = new();

    public void Save(AssistantSettings settings, AssistantKeyEdit key)
    {
        Saved = settings;
        SavedKey = key;
        if (key is not AssistantKeyEdit.Keep) Writes.Add(key);
        _settings.Value = settings;

        // The real store writes the edit into the named provider's own slot and nowhere else.
        switch (key)
        {
            case AssistantKeyEdit.Store store:
                SetSavedKey(store.Provider, store.Key);
                break;
            case AssistantKeyEdit.Forget forget:
                SetSavedKey(forget.Provider, null);
                break;
            default:
                Publish();
                break;
        }
    }

    /// <summary>Forces every role's answer, until the next key change recomputes it.</summary>
    public void SetConfigured(bool configured)
    {
        foreach (var state in _configured.Values) state.Value = configured;
    }

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
        foreach (var (role, state) in _configured)
            state.Value = _keys.Value.For(_settings.Value.ModelFor(role).Provider).IsUsable;
    }

    public void Dispose()
    {
        _active.Dispose();
        _commitMessage.Dispose();
        _settings.Dispose();
        foreach (var state in _configured.Values) state.Dispose();
        _keys.Dispose();
    }
}
