using GitBench.Controls;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Drives the assistant surfaces — the launcher, the overlay, the panel inside it, and the commit
/// bar's quick actions — by projecting the active repository's session from
/// <see cref="IAssistantSessionStore"/> and holding what belongs to the view rather than the
/// conversation: whether the overlay is up, the message being typed, and the connection being edited.
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
    private readonly State<bool> _settingsOpen = new(false);
    private readonly State<string> _providerDraft;
    private readonly State<string> _modelDraft;
    private readonly State<string> _baseUrlDraft;
    private readonly State<string> _keyDraft = new(string.Empty);
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
    private readonly Derived<bool> _showSettings;
    private readonly Derived<bool> _wantsKey;
    private readonly Derived<bool> _keyOptional;
    private readonly Derived<bool> _wantsBaseUrl;
    private readonly Derived<bool> _hasModelPresets;
    private readonly Derived<string> _providerName;
    private readonly Derived<string> _activeProviderName;
    private readonly Derived<string> _modelHint;
    private readonly Derived<string> _baseUrlHint;
    private readonly Derived<string> _keyHint;
    private readonly IDisposable _availableSub;
    private readonly IDisposable _selectionSub;

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
        _providerDraft = new State<string>(settings.ProviderId);
        _modelDraft = new State<string>(settings.Model ?? string.Empty);
        _baseUrlDraft = new State<string>(settings.BaseUrl ?? string.Empty);
        _keyFieldProviderId = settings.ProviderId;
        _available = new Derived<bool>(() => store.Active.Value is not null);
        _busy = new Derived<bool>(() => store.Active.Value?.IsBusy.Value ?? false);
        _thinking = new Derived<bool>(() => store.Active.Value?.IsThinking.Value ?? false);
        _needsSetup = new Derived<bool>(() => !store.IsConfigured.Value);
        _canClear = new Derived<bool>(() => (store.Active.Value?.Rows.Count ?? 0) > 0);
        _isEmpty = new Derived<bool>(() => !_canClear.Value);
        _canSend = new Derived<bool>(() =>
            store.Active.Value is not null
            && !store.Active.Value.IsBusy.Value
            && store.IsConfigured.Value
            && !string.IsNullOrWhiteSpace(_draft.Value));
        _generatingMessage = new Derived<bool>(() => store.CommitMessage.Value?.IsBusy.Value ?? false);
        _canGenerateMessage = new Derived<bool>(() =>
            store.CommitMessage.Value is not null
            && !store.CommitMessage.Value.IsBusy.Value
            && store.IsConfigured.Value);
        _canReviewBranch = new Derived<bool>(() =>
            store.Active.Value is not null
            && !store.Active.Value.IsBusy.Value
            && store.IsConfigured.Value);

        // Onboarding and settings are the same card: one is the other with nothing configured yet.
        _showSettings = new Derived<bool>(() => _settingsOpen.Value || !store.IsConfigured.Value);
        _wantsKey = new Derived<bool>(() => DraftProvider.AcceptsApiKey);
        _keyOptional = new Derived<bool>(() => !DraftProvider.RequiresApiKey);
        _wantsBaseUrl = new Derived<bool>(() => DraftProvider.CustomBaseUrl);
        _hasModelPresets = new Derived<bool>(() => DraftProvider.ModelPresets.Count > 0);
        _providerName = new Derived<string>(() => DraftProvider.DisplayName);
        _activeProviderName = new Derived<string>(() => store.Settings.Value.Provider.DisplayName);
        _modelHint = new Derived<string>(() => DraftProvider.ChatModel);
        _baseUrlHint = new Derived<string>(() => DraftProvider.BaseUrl);
        _keyHint = new Derived<string>(KeyHintText);

        Toggle = new Command(ToggleOpen, _available);
        Open = new Command(() => _open.Value = true, _available);
        Close = new Command(() => _open.Value = false);
        Send = new Command(SendDraft, _canSend);
        Stop = new Command(() => store.Active.Value?.Cancel(), _busy);
        ClearConversation = new Command(ClearActiveConversation, _canClear);
        GenerateCommitMessage = new Command(() => store.CommitMessage.Value?.Run(), _canGenerateMessage);
        ReviewBranch = new Command(RunBranchReview, _canReviewBranch);
        OpenSettings = new Command(ShowSettingsCard);
        // Dismissing the card is only offered once there is a working connection behind it.
        CloseSettings = new Command(() => _settingsOpen.Value = false, store.IsConfigured);
        SaveSettings = new Command(ApplySettings);

        // The toolset cannot be built without a repo, so losing the active one closes the overlay
        // rather than leaving a panel up with nothing behind it.
        _availableSub = _available.Subscribe(available =>
        {
            if (!available) _open.Value = false;
        });

        _selectionSub = bus.SubscribeScoped<AskAssistantAboutSelectionMessage>(AskAboutSelection);
    }

    // The diff's quick actions land here. A preset runs at once and answers in the transcript
    // without joining the thread; the free-form one only fills the composer, because the question is
    // still the person's to write and sending it is still their move.
    private void AskAboutSelection(AskAssistantAboutSelectionMessage m)
    {
        if (!_available.Value) return;
        _open.Value = true;

        if (m.AgentName is { Length: > 0 } agent)
        {
            _store.RunPreset(agent, m.Prompt);
            return;
        }

        _draft.Value = m.Prompt + "\n\n";
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

    /// <summary>True until a connection resolves — a key for a provider that needs one, or simply a
    /// provider that does not. The panel shows the connection card instead of the input.</summary>
    public IReadable<bool> NeedsSetup => _needsSetup;

    /// <summary>True while the transcript has nothing in it, for the panel's resting hint.</summary>
    public IReadable<bool> IsEmpty => _isEmpty;

    public IReadable<string> Draft => _draft;

    /// <summary>True while a commit message is being written, for the commit bar's spinner.</summary>
    public IReadable<bool> IsGeneratingMessage => _generatingMessage;

    /// <summary>True while the panel shows the connection card instead of the composer — either
    /// because nothing is configured yet, or because the user opened it.</summary>
    public IReadable<bool> ShowSettings => _showSettings;

    /// <summary>The connection being edited. Applied on save, so picking a provider does not repoint
    /// a conversation mid-thought. The three text fields are writable because that is what a two-way
    /// bound field binds to.</summary>
    public IReadable<string> ProviderDraft => _providerDraft;
    public State<string> ModelDraft => _modelDraft;
    public State<string> BaseUrlDraft => _baseUrlDraft;
    public State<string> KeyDraft => _keyDraft;

    /// <summary>Whether the card offers a key field for the draft provider. Asked of what the
    /// endpoint will take, not of what it demands: a self-hosted one takes a key without needing
    /// one, and leaving the field off is what pushes a gateway token into the endpoint box.</summary>
    public IReadable<bool> WantsApiKey => _wantsKey;

    /// <summary>Whether a key for the draft provider is worth offering but not needed, so the field
    /// says so rather than asking for something the user usually does not have.</summary>
    public IReadable<bool> IsApiKeyOptional => _keyOptional;

    /// <summary>Whether the draft provider's endpoint is the user's to set.</summary>
    public IReadable<bool> WantsBaseUrl => _wantsBaseUrl;

    /// <summary>Whether the draft provider publishes models worth offering. False for the endpoints
    /// that serve whatever the user loaded, where the field is free text and nothing else.</summary>
    public IReadable<bool> HasModelPresets => _hasModelPresets;

    public IReadable<string> ProviderName => _providerName;

    /// <summary>The provider the assistant is actually pointed at, for the panel header's switcher —
    /// which reports the connection in use rather than the one being edited.</summary>
    public IReadable<string> ActiveProviderName => _activeProviderName;

    /// <summary>The draft provider's own model, shown as the placeholder for "leave it alone".</summary>
    public IReadable<string> ModelHint => _modelHint;

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

    public ICommand OpenSettings { get; }
    public ICommand CloseSettings { get; }
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
            new RepoBarContextMenu.Item(
                s.AssistantChat,
                Open.Execute,
                LucideIcons.SquareTerminal,
                Enabled: Open.CanExecute.Value),
        ];
    }

    /// <summary>The provider list, marked with the one being edited and saying what each already has
    /// for a key — the card asks for one, so which providers are already answered for belongs in the
    /// same list.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildProviderMenu()
    {
        var current = _providerDraft.Value;
        var keys = _store.Keys.Value;
        return AssistantProviders.All
            .Select(provider => new RepoBarContextMenu.Item(
                provider.DisplayName,
                () => SetProviderDraft(provider.Id),
                Checked: string.Equals(provider.Id, current, StringComparison.Ordinal),
                Shortcut: KeyStateLabel(keys.For(provider))))
            .ToArray();
    }

    /// <summary>
    /// The panel header's switcher: the providers that can actually answer, marked with the active
    /// one, and a way to set up any that cannot. A provider with no key is left off rather than
    /// offered as a selection that would fail on the next turn.
    /// </summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildProviderSwitcher()
    {
        var keys = _store.Keys.Value;
        var active = _store.Settings.Value.ProviderId;
        var items = AssistantProviders.All
            .Where(provider => keys.For(provider).IsUsable)
            .Select(provider => new RepoBarContextMenu.Item(
                provider.DisplayName,
                () => SwitchProvider(provider.Id),
                Checked: string.Equals(provider.Id, active, StringComparison.Ordinal),
                Shortcut: KeyStateLabel(keys.For(provider))))
            .ToList();

        if (items.Count > 0) items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(
            _loc.Strings.Value.AssistantProviderConfigure, ShowSettingsCard, LucideIcons.Settings));
        return items;
    }

    /// <summary>Points the assistant at a provider that is already set up, without a trip through the
    /// card. Nothing here types a key, so nothing here saves one; a provider that is not set up
    /// opens the card on itself instead of becoming a connection that cannot sign a request.</summary>
    public void SwitchProvider(string providerId)
    {
        var provider = AssistantProviders.Resolve(providerId);
        var settings = _store.Settings.Value;

        if (!_store.Keys.Value.For(provider).IsUsable)
        {
            ShowSettingsCard();
            SetProviderDraft(provider.Id);
            return;
        }

        if (string.Equals(provider.Id, settings.ProviderId, StringComparison.Ordinal)) return;

        _store.Save(settings.Select(provider.Id), apiKey: null);
        SeedDrafts();
    }

    // The trailing slot the menu draws muted. A provider row carries no gesture, and what it has for
    // a key is what belongs there — masking leaves the card no other way to say it.
    private string? KeyStateLabel(AssistantKeyState state)
    {
        var s = _loc.Strings.Value;
        return state.Source switch
        {
            AssistantKeySource.Saved => s.AssistantProviderKeySaved,
            AssistantKeySource.Environment => s.AssistantProviderKeyEnvironment,
            AssistantKeySource.NotRequired => null,
            _ => s.AssistantProviderKeyMissing,
        };
    }

    /// <summary>The draft provider's models, marked with the one in the field. A default rather than
    /// a whitelist: picking fills the field in, and a model typed instead is kept as typed.</summary>
    public IReadOnlyList<RepoBarContextMenu.Item> BuildModelMenu()
    {
        var current = _modelDraft.Value.Trim();
        return DraftProvider.ModelPresets
            .Select(model => new RepoBarContextMenu.Item(
                model,
                () => _modelDraft.Value = model,
                Checked: string.Equals(model, current, StringComparison.Ordinal)))
            .ToArray();
    }

    public void SetDraft(string text) => _draft.Value = text;

    /// <summary>Picks a provider to configure, restoring the model, endpoint and key it was last
    /// given. None of the three travels from the provider being left behind.</summary>
    public void SetProviderDraft(string providerId)
    {
        var provider = AssistantProviders.Resolve(providerId);
        if (provider.Id == _providerDraft.Value) return;

        var choice = _store.Settings.Value.ChoiceFor(provider.Id);
        _providerDraft.Value = provider.Id;
        _modelDraft.Value = choice.Model ?? string.Empty;
        _baseUrlDraft.Value = choice.BaseUrl ?? string.Empty;
        FillKeyField(provider);
    }

    private AssistantProvider DraftProvider => AssistantProviders.Resolve(_providerDraft.Value);

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
        var provider = DraftProvider;
        return _store.Keys.Value.For(provider).Source switch
        {
            // A saved key is in the field, so there is nothing left for a line of prose to add.
            AssistantKeySource.Saved => string.Empty,
            AssistantKeySource.Environment =>
                s.AssistantSettingsKeyEnvironment(provider.EnvironmentVariable ?? string.Empty),
            // No key and none needed — but the box above is there for one, so it says what it is for
            // rather than reading as a question left unanswered.
            AssistantKeySource.NotRequired => s.AssistantSettingsKeyNotRequired(provider.DisplayName),
            _ => s.AssistantSettingsKeyNone,
        };
    }

    private void ShowSettingsCard()
    {
        SeedDrafts();
        _settingsOpen.Value = true;
        _open.Value = true;
    }

    // Starts the card from the connection in use, rather than from whatever an abandoned edit left
    // in the fields.
    private void SeedDrafts()
    {
        var settings = _store.Settings.Value;
        _providerDraft.Value = settings.ProviderId;
        _modelDraft.Value = settings.Model ?? string.Empty;
        _baseUrlDraft.Value = settings.BaseUrl ?? string.Empty;
        FillKeyField(settings.Provider);
    }

    private void ApplySettings()
    {
        var providerId = _providerDraft.Value;
        var key = KeyEdit(providerId);
        _store.Save(
            _store.Settings.Value.With(providerId, _modelDraft.Value, _baseUrlDraft.Value),
            key);
        _keyFieldProviderId = providerId;
        _keyHoldsTheStoredOne = key is { Length: > 0 };
        _settingsOpen.Value = false;
    }

    // Null leaves the stored key alone, empty forgets it. Emptying the box only means a deletion
    // where the box was holding this provider's stored key to begin with — and whatever the box
    // holds, it is not saved under a provider it was not filled for. That is the one rule: a key is
    // written for the provider it was typed or read for, and for no other.
    private string? KeyEdit(string providerId)
    {
        if (!string.Equals(_keyFieldProviderId, providerId, StringComparison.Ordinal)) return null;

        var typed = _keyDraft.Value;
        if (!string.IsNullOrWhiteSpace(typed)) return typed;
        return _keyHoldsTheStoredOne ? string.Empty : null;
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
        _selectionSub.Dispose();
        _availableSub.Dispose();
        _keyHint.Dispose();
        _baseUrlHint.Dispose();
        _modelHint.Dispose();
        _providerName.Dispose();
        _hasModelPresets.Dispose();
        _activeProviderName.Dispose();
        _wantsBaseUrl.Dispose();
        _keyOptional.Dispose();
        _wantsKey.Dispose();
        _showSettings.Dispose();
        _keyDraft.Dispose();
        _baseUrlDraft.Dispose();
        _modelDraft.Dispose();
        _providerDraft.Dispose();
        _settingsOpen.Dispose();
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
