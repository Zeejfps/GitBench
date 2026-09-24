using GitBench.Controls;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Features.Settings;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Drives the assistant surfaces — the launcher, the overlay, the panel inside it, and the commit
/// bar's quick actions — by projecting the active repository's session from
/// <see cref="IAssistantSessionStore"/> and holding what belongs to the view rather than the
/// conversation: whether the overlay is up, the message being typed, and the settings being edited.
/// </summary>
internal sealed class AssistantViewModel : IDisposable
{
    private const string ReviewAsk =
        "Review my changes — what is uncommitted in the working tree, and what the checked-out "
        + "branch adds on top of its base.";

    private readonly IAssistantSessionStore _store;
    private readonly ILocalizationService _loc;
    private readonly IMessageBus _bus;
    private readonly State<bool> _open = new(false);
    private readonly State<string> _draft = new(string.Empty);
    private readonly State<string> _keyProviderDraft;
    private readonly State<string> _baseUrlDraft;
    private readonly State<string> _keyDraft = new(string.Empty);
    private readonly IReadOnlyDictionary<AssistantRole, AssistantRoleDraft> _roles;
    private readonly Derived<bool> _available;
    private readonly Derived<bool> _busy;
    private readonly Derived<bool> _thinking;
    private readonly Derived<bool> _needsSetup;
    private readonly Derived<bool> _isEmpty;
    private readonly Derived<bool> _canClear;
    private readonly Derived<bool> _canSend;
    private readonly Derived<bool> _generatingMessage;
    private readonly Derived<bool> _canGenerateMessage;
    private readonly Derived<bool> _canReviewBranch;
    private readonly Derived<bool> _keyOptional;
    private readonly Derived<bool> _wantsBaseUrl;
    private readonly Derived<string> _keyProviderName;
    private readonly Derived<string> _activeProviderName;
    private readonly Derived<string> _baseUrlHint;
    private readonly Derived<string> _keyHint;
    private readonly IDisposable _availableSub;

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

    public AssistantViewModel(IAssistantSessionStore store, ILocalizationService loc, IMessageBus bus)
    {
        _store = store;
        _loc = loc;
        _bus = bus;
        var settings = store.Settings.Value;
        var chat = store.IsConfigured(AssistantRole.General);
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
        _available = new Derived<bool>(() => store.Active.Value is not null);
        _busy = new Derived<bool>(() => store.Active.Value?.IsBusy.Value ?? false);
        _thinking = new Derived<bool>(() => store.Active.Value?.IsThinking.Value ?? false);
        _needsSetup = new Derived<bool>(() => !chat.Value);
        _canClear = new Derived<bool>(() => (store.Active.Value?.Rows.Count ?? 0) > 0);
        _isEmpty = new Derived<bool>(() => !_canClear.Value);
        _canSend = new Derived<bool>(() =>
            store.Active.Value is not null
            && !store.Active.Value.IsBusy.Value
            && chat.Value
            && !string.IsNullOrWhiteSpace(_draft.Value));
        _generatingMessage = new Derived<bool>(() => store.CommitMessage.Value?.IsBusy.Value ?? false);
        _canGenerateMessage = new Derived<bool>(() =>
            store.CommitMessage.Value is not null
            && !store.CommitMessage.Value.IsBusy.Value
            && store.IsConfigured(AssistantRole.CommitMessage).Value);
        _canReviewBranch = new Derived<bool>(() =>
            store.Active.Value is not null
            && !store.Active.Value.IsBusy.Value
            && store.IsConfigured(AssistantRole.Review).Value);

        _keyOptional = new Derived<bool>(() => !KeyProvider.RequiresApiKey);
        _wantsBaseUrl = new Derived<bool>(() => KeyProvider.Hosting is AssistantHosting.SelfHosted);
        _keyProviderName = new Derived<string>(() => KeyProvider.DisplayName);
        _activeProviderName = new Derived<string>(() =>
            store.Settings.Value.ModelFor(AssistantRole.General).Provider.DisplayName);
        _baseUrlHint = new Derived<string>(() => KeyProvider.BaseUrl);
        _keyHint = new Derived<string>(KeyHintText);

        Toggle = new Command(ToggleOpen, _available);
        Open = new Command(() => _open.Value = true, _available);
        Close = new Command(() => _open.Value = false);
        Send = new Command(SendDraft, _canSend);
        Stop = new Command(() => store.Active.Value?.Cancel(), _busy);
        ClearConversation = new Command(ClearActiveConversation, _canClear);
        GenerateCommitMessage = new Command(() => store.CommitMessage.Value?.Run(), _canGenerateMessage);
        ReviewBranch = new Command(RunBranchReview, _canReviewBranch);
        OpenSettings = new Command(OpenSettingsWindow);
        ResetSettings = new Command(SeedDrafts);
        SaveSettings = new Command(ApplySettings);

        // The toolset cannot be built without a repo, so losing the active one closes the overlay
        // rather than leaving a panel up with nothing behind it.
        _availableSub = _available.Subscribe(available =>
        {
            if (!available) _open.Value = false;
        });

    }

    // The diff's quick actions land here. A preset (agentName) runs at once and answers in the
    // transcript without joining the thread; the free-form one (null) only fills the composer,
    // because the question is still the person's to write and sending it is still their move.
    public void AskAboutSelection(string? agentName, string prompt)
    {
        if (!_available.Value) return;
        _open.Value = true;

        if (agentName is { Length: > 0 } agent)
        {
            _store.RunPreset(agent, prompt);
            return;
        }

        _draft.Value = prompt + "\n\n";
    }

    // The review runs the moment it is picked, in the overlay, as a one-shot detached from the
    // thread — the same shape as the diff's presets. The ask is addressed to the model rather than
    // read by anyone, so it is written here in English like the diff's are. What there is to review
    // is the agent's to work out from its tools: the uncommitted work and the branch's own commits
    // are both in scope, either can be empty, and neither is a thing this menu could pin down at the
    // moment it was opened.
    private void RunBranchReview()
    {
        _open.Value = true;
        _store.RunPreset(AgentCatalog.ReviewBranchAgent, ReviewAsk);
    }

    /// <summary>The active repo's conversation, or null when no repo is active.</summary>
    public IReadable<AssistantSession?> Session => _store.Active;

    public IReadable<bool> IsOpen => _open;

    /// <summary>Whether the assistant can be offered at all — false on the welcome screen and
    /// whenever no repository is active.</summary>
    public IReadable<bool> IsAvailable => _available;

    public IReadable<bool> IsBusy => _busy;

    public IReadable<bool> IsThinking => _thinking;

    /// <summary>True until the chat's connection resolves — a key for a provider that needs one, or
    /// simply a provider that does not. The panel shows the settings card instead of the input.</summary>
    public IReadable<bool> NeedsSetup => _needsSetup;

    /// <summary>True while the transcript has nothing in it, for the panel's resting hint.</summary>
    public IReadable<bool> IsEmpty => _isEmpty;

    public IReadable<string> Draft => _draft;

    /// <summary>True while a commit message is being written, for the commit bar's spinner.</summary>
    public IReadable<bool> IsGeneratingMessage => _generatingMessage;

    /// <summary>True while the panel shows the settings card instead of the composer, which is
    /// while nothing is configured yet: once the chat can send, the card lives in the settings
    /// window and the panel keeps its composer.</summary>
    public IReadable<bool> ShowSettings => _needsSetup;

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

    /// <summary>The provider the chat is actually pointed at, for the panel header's switcher —
    /// which reports the connection in use rather than the one being edited.</summary>
    public IReadable<string> ActiveProviderName => _activeProviderName;

    public IReadable<string> BaseUrlHint => _baseUrlHint;

    /// <summary>What is known about the key in effect — saved, inherited from the environment, or
    /// missing — so the card does not ask again for something it already has.</summary>
    public IReadable<string> KeyHint => _keyHint;

    public ICommand Toggle { get; }
    public ICommand Open { get; }
    public ICommand Close { get; }
    public ICommand Send { get; }
    public ICommand Stop { get; }

    /// <summary>Starts the active repository's conversation over. Offered only while there is one to
    /// discard, and recoverable from the toast it raises.</summary>
    public ICommand ClearConversation { get; }

    public ICommand GenerateCommitMessage { get; }

    /// <summary>Reviews the work in front of the person — what is uncommitted, and what the
    /// checked-out branch adds on top of its base — answering in the transcript. Offered whenever the
    /// assistant can answer at all and this repository's conversation is not already mid-turn.</summary>
    public ICommand ReviewBranch { get; }

    /// <summary>Opens the settings window on the assistant's page.</summary>
    public ICommand OpenSettings { get; }
    /// <summary>Reloads saved connection values without opening or closing chat.</summary>
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
            new RepoBarContextMenu.Item(
                s.AssistantReviewBranch,
                ReviewBranch.Execute,
                LucideIcons.Search,
                Enabled: ReviewBranch.CanExecute.Value),
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

    /// <summary>
    /// The panel header's switcher: the providers that can actually answer the chat, marked with the
    /// one it is on, and a way to set up any that cannot. A provider with no key is left off rather
    /// than offered as a selection that would fail on the next turn.
    /// </summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildProviderSwitcher()
    {
        var keys = _store.Keys.Value;
        var s = _loc.Strings.Value;
        var active = _store.Settings.Value.ModelFor(AssistantRole.General).Provider.Id;
        var items = AssistantProviders.All
            .Where(provider => keys.For(provider).IsUsable)
            .Select(provider => new RepoBarContextMenu.Item(
                provider.DisplayName,
                () => SwitchProvider(provider.Id),
                Checked: string.Equals(provider.Id, active, StringComparison.Ordinal),
                Shortcut: AssistantKeyLabels.For(keys.For(provider), s)))
            .ToList();

        if (items.Count > 0) items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            s.AssistantProviderConfigure, OpenSettingsWindow, LucideIcons.Settings));
        return items;
    }

    /// <summary>Points the chat at a provider that is already set up, on that provider's default
    /// model, without a trip through the settings. Nothing here types a key, so nothing here saves
    /// one; a provider that is not set up opens the settings instead of becoming a connection that
    /// cannot sign a request. The other roles stay where they are.</summary>
    public void SwitchProvider(string providerId)
    {
        var provider = AssistantProviders.Resolve(providerId);
        var settings = _store.Settings.Value;

        if (!_store.Keys.Value.For(provider).IsUsable)
        {
            OpenSettingsWindow();
            return;
        }

        if (string.Equals(provider.Id, settings.ModelFor(AssistantRole.General).Provider.Id, StringComparison.Ordinal))
            return;

        _store.Save(settings.WithModel(AssistantRole.General, provider.Id, null), AssistantKeyEdit.None);
        SeedDrafts();
    }

    public void SetDraft(string text) => _draft.Value = text;

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

    private void OpenSettingsWindow() => _bus.Broadcast(new OpenSettingsWindowMessage(SettingsPage.Agent));

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

    private void ToggleOpen() => _open.Value = !_open.Value;

    // The undo rides the same feedback slot every other cheap-but-destructive action uses, rather
    // than a modal: a thread the user can put back is not worth stopping them for. The stored key is
    // untouched — it is not part of the conversation, and losing it would restage onboarding.
    private void ClearActiveConversation()
    {
        var session = _store.Active.Value;
        if (session is null) return;

        session.Clear();
        var strings = _loc.Strings.Value;
        _bus.Broadcast(new ShowToastMessage(ToastIntent.Success(
            strings.AssistantCleared,
            new ToastAction(strings.AssistantClearUndo, session.UndoClear))));
    }

    private void SendDraft()
    {
        var text = _draft.Value;

        // Cleared first so the two-way bound field empties before the row for it appears.
        _draft.Value = string.Empty;
        _store.Active.Value?.Send(text);
    }

    public void Dispose()
    {
        _availableSub.Dispose();
        _keyHint.Dispose();
        _baseUrlHint.Dispose();
        _keyProviderName.Dispose();
        _activeProviderName.Dispose();
        _wantsBaseUrl.Dispose();
        _keyOptional.Dispose();
        foreach (var draft in _roles.Values) draft.Dispose();
        _keyDraft.Dispose();
        _baseUrlDraft.Dispose();
        _keyProviderDraft.Dispose();
        _canReviewBranch.Dispose();
        _canGenerateMessage.Dispose();
        _generatingMessage.Dispose();
        _canSend.Dispose();
        _canClear.Dispose();
        _isEmpty.Dispose();
        _needsSetup.Dispose();
        _thinking.Dispose();
        _busy.Dispose();
        _available.Dispose();
        _draft.Dispose();
        _open.Dispose();
    }
}
