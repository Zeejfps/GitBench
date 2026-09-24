using GitBench.Controls;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Repos;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Drives the assistant's settings and the commit bar's commit message, from
/// <see cref="IAssistantSessionStore"/>: the connection being edited, and whether a message is being
/// written.
/// </summary>
internal sealed class AssistantViewModel : IDisposable
{
    private readonly IAssistantSessionStore _store;
    private readonly ILocalizationService _loc;
    private readonly State<string> _keyProviderDraft;
    private readonly State<string> _baseUrlDraft;
    private readonly State<string> _keyDraft = new(string.Empty);
    private readonly IReadOnlyDictionary<AssistantRole, AssistantRoleDraft> _roles;
    private readonly Derived<bool> _generatingMessage;
    private readonly Derived<bool> _canGenerateMessage;
    private readonly Derived<bool> _keyOptional;
    private readonly Derived<bool> _wantsBaseUrl;
    private readonly Derived<string> _keyProviderName;
    private readonly Derived<string> _baseUrlHint;
    private readonly Derived<string> _keyHint;

    // The endpoints edited for providers the picker has moved away from, applied on save alongside
    // the one in the field. An endpoint belongs to its provider, so leaving a provider does not
    // discard what was typed for it the way leaving discards an unsaved key.
    private AssistantSettings _settingsDraft;

    // Which provider the key field's contents are for, and whether they are that provider's stored
    // key rather than something typed. Only the stored case makes emptying the box a deletion — an
    // untouched blank box has never stood for one — and neither case is ever saved under a provider
    // the field was not filled for.
    private string _keyFieldProviderId;
    private bool _keyHoldsTheStoredOne;

    public AssistantViewModel(IAssistantSessionStore store, ILocalizationService loc)
    {
        _store = store;
        _loc = loc;
        var settings = store.Settings.Value;
        _settingsDraft = settings;
        var keyProvider = settings.ModelFor(AssistantRole.General).Provider;
        _keyProviderDraft = new State<string>(keyProvider.Id);
        _baseUrlDraft = new State<string>(settings.BaseUrlFor(keyProvider) ?? string.Empty);
        _keyFieldProviderId = keyProvider.Id;
        var roles = new Dictionary<AssistantRole, AssistantRoleDraft>();
        foreach (var role in AssistantRoles.All)
        {
            var draft = new AssistantRoleDraft(role, store, loc);
            draft.Seed(settings.ModelFor(role));
            roles[role] = draft;
        }
        _roles = roles;
        _generatingMessage = new Derived<bool>(() => store.CommitMessage.Value?.IsBusy.Value ?? false);
        _canGenerateMessage = new Derived<bool>(() =>
            store.CommitMessage.Value is not null
            && !store.CommitMessage.Value.IsBusy.Value
            && store.IsConfigured(AssistantRole.CommitMessage).Value);
        _keyOptional = new Derived<bool>(() => !KeyProvider.RequiresApiKey);
        _wantsBaseUrl = new Derived<bool>(() => KeyProvider.Hosting is AssistantHosting.SelfHosted);
        _keyProviderName = new Derived<string>(() => KeyProvider.DisplayName);
        _baseUrlHint = new Derived<string>(() => KeyProvider.BaseUrl);
        _keyHint = new Derived<string>(KeyHintText);

        GenerateCommitMessage = new Command(() => store.CommitMessage.Value?.Run(), _canGenerateMessage);
        ResetSettings = new Command(SeedDrafts);
        SaveSettings = new Command(ApplySettings);
    }

    /// <summary>True while a commit message is being written, for the commit bar's spinner.</summary>
    public IReadable<bool> IsGeneratingMessage => _generatingMessage;

    /// <summary>The provider whose key and endpoint are in the card's fields. Applied on save, so
    /// nothing typed repoints a conversation mid-thought. The two text fields are writable because
    /// that is what a two-way bound field binds to.</summary>
    public IReadable<string> KeyProviderDraft => _keyProviderDraft;
    public State<string> BaseUrlDraft => _baseUrlDraft;
    public State<string> KeyDraft => _keyDraft;

    /// <summary>Each role's line of the card: the provider and model it will run on once saved.</summary>
    public AssistantRoleDraft RoleDraft(AssistantRole role) => _roles[role];

    /// <summary>Whether a key for the provider being edited is worth offering but not needed, so the
    /// field says so rather than asking for something the user usually does not have.</summary>
    public IReadable<bool> IsApiKeyOptional => _keyOptional;

    /// <summary>Whether the endpoint of the provider being edited is the user's to set.</summary>
    public IReadable<bool> WantsBaseUrl => _wantsBaseUrl;

    public IReadable<string> KeyProviderName => _keyProviderName;

    public IReadable<string> BaseUrlHint => _baseUrlHint;

    /// <summary>What is known about the key in effect — saved, inherited from the environment, or
    /// missing — so the card does not ask again for something it already has.</summary>
    public IReadable<string> KeyHint => _keyHint;

    public ICommand GenerateCommitMessage { get; }

    /// <summary>Reloads saved connection values.</summary>
    public ICommand ResetSettings { get; }
    public ICommand SaveSettings { get; }

    /// <summary>The commit bar's assistant menu, built per open so a generation already running
    /// shows as running rather than as an item that would start a second one.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildCommitMenu()
    {
        var s = _loc.Strings.Value;
        return
        [
            new RepoBarContextMenu.Item(
                _generatingMessage.Value ? s.AssistantGeneratingMessage : s.AssistantGenerateMessage,
                GenerateCommitMessage.Execute,
                LucideIcons.PencilLine,
                Enabled: GenerateCommitMessage.CanExecute.Value),
        ];
    }

    /// <summary>The provider list for the key picker, marked with the one being edited and saying
    /// what each already has for a key — the card asks for one, so which providers are already
    /// answered for belongs in the same list.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildProviderMenu()
    {
        var current = _keyProviderDraft.Value;
        var keys = _store.Keys.Value;
        var s = _loc.Strings.Value;
        return AssistantProviders.All
            .Select(provider => new RepoBarContextMenu.Item(
                provider.DisplayName,
                () => SetKeyProviderDraft(provider.Id),
                Checked: string.Equals(provider.Id, current, StringComparison.Ordinal),
                Shortcut: AssistantKeyLabels.For(keys.For(provider), s)))
            .ToArray();
    }

    /// <summary>Picks a provider to give a key and endpoint, restoring what it was last given.
    /// Neither travels from the provider being left behind: its endpoint is kept aside for the save,
    /// and a key typed for it but not saved is dropped. Roles pointed at a provider that cannot
    /// answer follow the pick, so setting up a first provider sets up every role with it.</summary>
    public void SetKeyProviderDraft(string providerId)
    {
        var provider = AssistantProviders.Resolve(providerId);
        if (provider.Id == _keyProviderDraft.Value) return;

        _settingsDraft = _settingsDraft.WithBaseUrl(KeyProvider.Id, _baseUrlDraft.Value);
        _keyProviderDraft.Value = provider.Id;
        _baseUrlDraft.Value = _settingsDraft.BaseUrlFor(provider) ?? string.Empty;
        FillKeyField(provider);
        FollowKeyProvider(_store.Keys.Value, provider);
    }

    // A role on a provider with no key would never answer, so it is pointed at the one being set up
    // instead. A role already on a provider that answers is left alone.
    private void FollowKeyProvider(AssistantKeyring keys, AssistantProvider target)
    {
        foreach (var draft in _roles.Values)
            if (!keys.For(draft.Provider).IsUsable)
                draft.SetProvider(target.Id);
    }

    private AssistantProvider KeyProvider => AssistantProviders.Resolve(_keyProviderDraft.Value);

    // This provider's stored key is shown rather than described — masked, and the framework refuses
    // the clipboard over a masked field, so it reads as bullets and leaves no other way out. Asked
    // for by provider, because bullets look the same whichever key they stand for and a field filled
    // from anything else would be saved back under the wrong one. A key inherited from the
    // environment is never filled in: the app does not own it, and a box the user could empty would
    // promise a deletion that is not the app's to make.
    private void FillKeyField(AssistantProvider provider)
    {
        var stored = _store.Keys.Value.For(provider).SavedKey;
        _keyFieldProviderId = provider.Id;
        _keyHoldsTheStoredOne = stored is not null;
        _keyDraft.Value = stored ?? string.Empty;
    }

    private string KeyHintText()
    {
        var s = _loc.Strings.Value;
        var provider = KeyProvider;
        return _store.Keys.Value.For(provider).Source switch
        {
            // A saved key is in the field, so there is nothing left for a line of prose to add.
            AssistantKeySource.Saved => string.Empty,
            AssistantKeySource.Environment when provider.Hosting is AssistantHosting.Hosted hosted =>
                s.AssistantSettingsKeyEnvironment(hosted.EnvironmentVariable),
            // No key and none needed — but the box above is there for one, so it says what it is for
            // rather than reading as a question left unanswered.
            AssistantKeySource.NotRequired => s.AssistantSettingsKeyNotRequired(provider.DisplayName),
            _ => s.AssistantSettingsKeyNone,
        };
    }

    // Starts the card from the settings in use, rather than from whatever an abandoned edit left in
    // the fields. The key picker opens on the chat's provider, the one most likely to be asked for.
    private void SeedDrafts()
    {
        var settings = _store.Settings.Value;
        var provider = settings.ModelFor(AssistantRole.General).Provider;
        _settingsDraft = settings;
        _keyProviderDraft.Value = provider.Id;
        _baseUrlDraft.Value = settings.BaseUrlFor(provider) ?? string.Empty;
        foreach (var draft in _roles.Values)
            draft.Seed(settings.ModelFor(draft.Role));
        FillKeyField(provider);
    }

    private void ApplySettings()
    {
        var provider = KeyProvider;
        var key = KeyEdit(provider);

        // A key just saved makes its provider answer, so roles left on one that cannot are pointed
        // at it before they are read: the first key given sets up every role, not one line of three.
        var keys = key.ApplyTo(_store.Keys.Value);
        if (keys.For(provider).IsUsable) FollowKeyProvider(keys, provider);

        var settings = _settingsDraft.WithBaseUrl(provider.Id, _baseUrlDraft.Value);
        foreach (var draft in _roles.Values)
            settings = settings.WithModel(draft.Role, draft.Choice);

        _store.Save(settings, key);
        _settingsDraft = settings;
        _keyFieldProviderId = provider.Id;
        _keyHoldsTheStoredOne = key is AssistantKeyEdit.Store;
    }

    // Emptying the box only means a deletion where the box was holding this provider's stored key
    // to begin with — and whatever the box holds, it is not saved under a provider it was not filled
    // for. That is the one rule: a key is written for the provider it was typed or read for, and for
    // no other. The stored key read back untouched is not an edit either, so saving the other
    // fields does not rewrite the secret store.
    private AssistantKeyEdit KeyEdit(AssistantProvider provider)
    {
        if (!string.Equals(_keyFieldProviderId, provider.Id, StringComparison.Ordinal)) return AssistantKeyEdit.None;

        var typed = _keyDraft.Value;
        if (string.IsNullOrWhiteSpace(typed))
            return _keyHoldsTheStoredOne ? new AssistantKeyEdit.Forget(provider) : AssistantKeyEdit.None;

        var stored = _store.Keys.Value.For(provider).SavedKey;
        if (_keyHoldsTheStoredOne && string.Equals(typed, stored, StringComparison.Ordinal)) return AssistantKeyEdit.None;
        return new AssistantKeyEdit.Store(provider, typed);
    }

    public void Dispose()
    {
        _keyHint.Dispose();
        _baseUrlHint.Dispose();
        _keyProviderName.Dispose();
        _wantsBaseUrl.Dispose();
        _keyOptional.Dispose();
        foreach (var draft in _roles.Values) draft.Dispose();
        _keyDraft.Dispose();
        _baseUrlDraft.Dispose();
        _keyProviderDraft.Dispose();
        _canGenerateMessage.Dispose();
        _generatingMessage.Dispose();
    }
}
