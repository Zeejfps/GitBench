using System.Diagnostics;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.CodeIntel;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Builds the backend one role's agents talk to. <paramref name="connection"/> is read per request,
/// so a provider, model or key changed after startup takes effect without rebuilding anything.
/// </summary>
internal delegate IAssistantBackend AssistantBackendFactory(AssistantRole role, Func<AssistantConnection> connection);

/// <summary>
/// The one place assistant conversations live: one per repository, in memory, for the app session.
/// View models project from it.
/// </summary>
internal interface IAssistantSessionStore
{
    /// <summary>The active repo's conversation, or null when no repo is active. Swaps on repo switch.</summary>
    IReadable<AssistantSession?> Active { get; }

    /// <summary>The active repo's "Generate commit message" action, or null when no repo is
    /// active.</summary>
    IReadable<CommitMessageQuickAction?> CommitMessage { get; }

    /// <summary>The model each role runs on, and the endpoint chosen for each provider.</summary>
    IReadable<AssistantSettings> Settings { get; }

    /// <summary>Whether a role can reach its model: a key resolved for its provider, or a provider
    /// that needs none.</summary>
    IReadable<bool> IsConfigured(AssistantRole role);

    /// <summary>What every provider has for a key, so a card or a switcher can say which are ready
    /// and hold the right one — always asked for by provider, never for "the" key.</summary>
    IReadable<AssistantKeyring> Keys { get; }

    /// <summary>Adopts new settings and settles one key edit. Every role's connection points at its
    /// new model before this returns, so nothing sent afterwards can reach the old one; the secret
    /// store is read and written off the UI thread, and a role whose key was not already known reads
    /// as unconfigured until it has landed.</summary>
    void Save(AssistantSettings settings, AssistantKeyEdit key);

    /// <summary>Runs a named one-shot agent over an already-composed prompt in the active repo's
    /// transcript. Does nothing when no repo is active, a turn is already running, or the assistant
    /// is not configured.</summary>
    void RunPreset(string agentName, string prompt);
}

/// <summary>
/// Owns every repository's assistant conversation, keyed by repo id, so switching away from a repo
/// and back returns to the same exchange. Conversations are session-only and never persisted.
/// </summary>
/// <remarks>
/// Mirrors <see cref="RepoOperationsStore"/>'s shape: per-repo state, an "active" projection that
/// swaps on repo switch, and a <see cref="Start"/> that wires the registry once the UI loop exists.
/// Credentials resolve off the UI thread — the OS secret store blocks and can raise an unlock
/// prompt, which on the UI thread would freeze the app on first open.
///
/// A review window's walkthrough runs in the conversation of the window's own repository — the one
/// its tools are bound to — whether or not that is the main window's active one; the rail is
/// where the reviewer reads it, and the transcript it also lands in is that repository's.
/// </remarks>
internal sealed class AssistantSessionStore : IAssistantSessionStore, IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly ISymbolExtractor _extractor;
    private readonly AssistantCredentials _credentials;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly IReviewProgressStore _reviewProgress;
    private readonly IReviewWindowRegistry _reviewWindows;
    private readonly AgentCatalog _catalog;
    private readonly AgentDefinition _agent;
    private readonly AgentDefinition _commitMessageAgent;
    private readonly AgentDefinition _walkthroughAgent;
    private readonly IReadOnlyDictionary<AssistantRole, IAssistantBackend> _backends;
    private readonly AssistantWriteSurface _writes;
    private readonly IMessageBus _bus;

    private readonly Dictionary<Guid, AssistantSession> _sessions = new();
    private readonly Dictionary<Guid, CommitMessageQuickAction> _commitMessages = new();
    private readonly Dictionary<Guid, AssistantWalkthroughNarration> _narrations = new();
    private readonly State<AssistantSession?> _active = new(null);
    private readonly State<CommitMessageQuickAction?> _activeCommitMessage = new(null);
    private readonly State<AssistantSettings> _settings;
    private readonly IReadOnlyDictionary<AssistantRole, State<bool>> _isConfigured;
    private readonly State<AssistantKeyring> _keys = new(AssistantKeyring.Empty);

    // Read by the backends from whatever thread a turn runs on; written only on the UI thread.
    private AssistantConnections _connections;

    // Counts the resolves asked for, so a slow one landing after a later one is dropped rather than
    // reinstating the provider and key it was asked about.
    private int _resolves;

    // Every resolve runs after the one before it has finished with the secret store. The counter
    // above orders the requests; this orders the reads and writes themselves, so a later pass's
    // read can never overtake an earlier pass's write and publish a keyring missing the key that
    // was just saved. Touched only on the UI thread, where every resolve is asked for.
    private Task _resolving = Task.CompletedTask;

    private IDisposable? _activeSub;
    private IDisposable? _narrateSub;
    private bool _started;
    private bool _disposed;

    public AssistantSessionStore(
        IRepoRegistry registry,
        IGitService git,
        ISymbolExtractor extractor,
        AssistantCredentials credentials,
        State<AssistantSettings> settings,
        ILocalizationService loc,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ICommitEditor commitEditor,
        IReviewProgressStore reviewProgress,
        IReviewWindowRegistry reviewWindows,
        IRepoOperationsStore operations,
        IDocumentStore documents,
        AssistantBackendFactory backendFactory)
    {
        _registry = registry;
        _git = git;
        _extractor = extractor;
        _credentials = credentials;
        _settings = settings;
        _loc = loc;
        _dispatcher = dispatcher;
        _reviewProgress = reviewProgress;
        _reviewWindows = reviewWindows;
        _bus = bus;
        _writes = new AssistantWriteSurface(dispatcher, bus, registry, commitEditor, operations, documents);
        _connections = AssistantConnections.Build(settings.Value, AssistantKeyring.Empty);
        _catalog = AgentCatalog.LoadEmbedded();
        _agent = _catalog.Get(AgentCatalog.GeneralAgent);
        _commitMessageAgent = _catalog.Get(AgentCatalog.CommitMessageAgent);
        _walkthroughAgent = _catalog.Get(AgentCatalog.WalkthroughReviewAgent);

        var backends = new Dictionary<AssistantRole, IAssistantBackend>();
        var configured = new Dictionary<AssistantRole, State<bool>>();
        foreach (var role in AssistantRoles.All)
        {
            backends[role] = backendFactory(role, () => Volatile.Read(ref _connections).For(role));
            configured[role] = new State<bool>(false);
        }
        _backends = backends;
        _isConfigured = configured;
    }

    public IReadable<AssistantSession?> Active => _active;

    public IReadable<CommitMessageQuickAction?> CommitMessage => _activeCommitMessage;

    public IReadable<AssistantSettings> Settings => _settings;

    public IReadable<bool> IsConfigured(AssistantRole role) => _isConfigured[role];

    public IReadable<AssistantKeyring> Keys => _keys;

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _narrateSub = _bus.SubscribeScoped<NarrateWalkthroughMessage>(OnNarrate);
        Resolve(_settings.Value, AssistantKeyEdit.None);
    }

    public void Save(AssistantSettings settings, AssistantKeyEdit key)
    {
        var previous = _settings.Value;

        // The connections move first, and without an await in front of them. Everything below
        // announces the switch — the header names the new provider, the transcript says it was
        // switched to — while the secret store is milliseconds away at best and a locked keyring is
        // seconds. A request signed between the announcement and the answer has to be the new
        // provider's.
        Publish(settings, key.ApplyTo(_keys.Value));

        _settings.Value = settings;
        RestartConversations(previous, settings);
        Resolve(settings, key);
    }

    // Every role's connection from one settings and one keyring, and whether each can send. Built
    // here and nowhere else, so the key that signs a request is by construction the key of the
    // provider it is sent to. Called with what is already known plus this save's edit, which is the
    // answer the resolve will come back with minus whatever only the secret store can add: a role
    // whose key is not known yet reads as unconfigured for the duration, which is what closes the
    // composer and the presets rather than letting them reach the provider being left behind.
    private void Publish(AssistantSettings settings, AssistantKeyring keys)
    {
        var connections = AssistantConnections.Build(settings, keys);
        Volatile.Write(ref _connections, connections);
        foreach (var role in AssistantRoles.All)
            _isConfigured[role].Value = connections.For(role).IsUsable;
    }

    // A tool-call id belongs to the provider that issued it — Anthropic's validator takes the id
    // literally and strict OpenAI-compatible endpoints refuse anything not in their own shape — and
    // the whole conversation is replayed on every turn. So the exchange the model is sent starts
    // again when its role moves provider. The transcript is untouched: what was said is still what
    // was said. The review is a one-shot with no exchange to carry, so it has nothing to restart.
    private void RestartConversations(AssistantSettings previous, AssistantSettings next)
    {
        if (ProviderMoved(previous, next, AssistantRole.General, out var chat))
        {
            var notice = _loc.Strings.Value.AssistantProviderSwitched(chat.DisplayName);
            foreach (var session in _sessions.Values)
                session.RestartForProviderChange(notice);
        }

        if (ProviderMoved(previous, next, AssistantRole.Walkthrough, out _))
            foreach (var narration in _narrations.Values)
                narration.RestartForProviderChange();
    }

    private static bool ProviderMoved(
        AssistantSettings previous, AssistantSettings next, AssistantRole role, out AssistantProvider provider)
    {
        provider = next.ModelFor(role).Provider;
        return !string.Equals(previous.ModelFor(role).Provider.Id, provider.Id, StringComparison.Ordinal);
    }

    // Reads (and optionally rewrites) the secret store on a worker, then posts the result back. What
    // ends up in effect is re-read rather than assumed: the environment fallback can outrank a save.
    private void Resolve(AssistantSettings settings, AssistantKeyEdit edit)
    {
        var credentials = _credentials;
        var dispatcher = _dispatcher;
        var resolve = ++_resolves;
        _resolving = _resolving.ContinueWith(
            _ =>
            {
                var keys = AssistantKeyring.Empty;
                try
                {
                    switch (edit)
                    {
                        case AssistantKeyEdit.Store store:
                            credentials.Save(store.Provider, store.Key);
                            break;
                        case AssistantKeyEdit.Forget forget:
                            credentials.Clear(forget.Provider);
                            break;
                        case AssistantKeyEdit.Keep:
                            break;
                        default:
                            throw new UnreachableException();
                    }
                    keys = credentials.Keyring();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Assistant] API key lookup failed: {ex.Message}");
                }

                // A secret store that refused the write still leaves the key in effect for this
                // session, and the card holds it: it is the app's own for as long as it is running.
                if (edit is AssistantKeyEdit.Store stored && keys.For(stored.Provider).SavedKey is null)
                    keys = edit.ApplyTo(keys);

                dispatcher.Post(() => Adopt(resolve, settings, keys));
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    // Confirms what Save already pointed the assistant at, now that the secret store has answered.
    // A resolve overtaken by a later one is dropped rather than reinstating what it asked about.
    private void Adopt(int resolve, AssistantSettings settings, AssistantKeyring keys)
    {
        if (_disposed || resolve != _resolves) return;
        Publish(settings, keys);
        _keys.Value = keys;
    }

    // Built per run rather than kept: a preset is a one-shot, and its loop carries the state of the
    // exchange it drove. Nothing is written by these agents, so they get the reads only.
    public void RunPreset(string agentName, string prompt)
    {
        if (_disposed) return;

        var agent = _catalog.Get(agentName);

        // A preset carries a diff and the checkout's path to whichever provider its role is on, and
        // it has no composer to grey out, so it asks for the same gate the composer is given.
        if (!_isConfigured[agent.Role].Value) return;

        if (_registry.Active.Value is not { } repo) return;
        if (_active.Value is not { } session) return;

        var toolset = AssistantToolset.ForRepo(_git, repo, _extractor, agent);
        session.RunPreset(prompt, Loop(agent, toolset));
    }

    private AssistantAgentLoop Loop(AgentDefinition agent, AssistantToolset toolset) =>
        new(_backends[agent.Role], agent, toolset, () => ToolCallingIsUnproven(agent.Role));

    // A review window's cue for the assistant as narrator. Runs in the window repository's own
    // conversation with the full toolset, since the presentation tools need the window registry.
    // With no key to answer with, the rail is told its narrator is gone rather than left saying
    // the assistant is working on something nothing will ever send.
    private void OnNarrate(NarrateWalkthroughMessage m)
    {
        if (_disposed) return;
        if (_registry.Repos.FirstOrDefault(r => r.Id == m.RepoId) is not { } repo) return;

        if (!_isConfigured[_walkthroughAgent.Role].Value)
        {
            _reviewWindows.LatestFor(repo.Id)?.Walkthrough.MarkDisconnected();
            return;
        }

        NarrationFor(repo).Cue(m.Cue);
    }

    private AssistantWalkthroughNarration NarrationFor(Repo repo)
    {
        if (_narrations.TryGetValue(repo.Id, out var existing)) return existing;

        var narration = new AssistantWalkthroughNarration(
            repo.Id,
            SessionFor(repo),
            observer =>
            {
                var toolset = AssistantToolset.ForRepo(
                    _git, repo, _extractor, _walkthroughAgent, _reviewProgress, _reviewWindows, _writes);
                return new AssistantThread(Loop(_walkthroughAgent, toolset), observer);
            },
            _reviewWindows,
            _dispatcher);
        _narrations[repo.Id] = narration;
        return narration;
    }

    private void OnActiveChanged()
    {
        if (_disposed) return;
        var repo = _registry.Active.Value;
        _active.Value = repo is null ? null : SessionFor(repo);
        _activeCommitMessage.Value = repo is null ? null : CommitMessageFor(repo);
    }

    private AssistantSession SessionFor(Repo repo)
    {
        if (_sessions.TryGetValue(repo.Id, out var existing)) return existing;

        // The toolset is bound to this one checkout, so the assistant cannot reach the others.
        var toolset = AssistantToolset.ForRepo(_git, repo, _extractor, _agent, _reviewProgress, _reviewWindows, _writes);
        var session = new AssistantSession(repo, _git, Loop(_agent, toolset), _loc, _dispatcher);
        _sessions[repo.Id] = session;
        return session;
    }

    // Keyed per repo like the conversations, so a generation started before a repo switch still
    // knows which checkout it was asked about when it comes back. The agent's allowed list is what
    // limits it: reads, plus the one write that fills the commit box it was asked to fill.
    private CommitMessageQuickAction CommitMessageFor(Repo repo)
    {
        if (_commitMessages.TryGetValue(repo.Id, out var existing)) return existing;

        var toolset = AssistantToolset.ForRepo(
            _git, repo, _extractor, _commitMessageAgent, _reviewProgress, _reviewWindows, _writes);
        var action = new CommitMessageQuickAction(
            repo,
            _git,
            Loop(_commitMessageAgent, toolset),
            _writes,
            _loc,
            _dispatcher);
        _commitMessages[repo.Id] = action;
        return action;
    }

    // Whether a turn that never calls a tool is worth reporting: against a self-hosted endpoint it
    // may mean the loaded model cannot call tools at all, which otherwise fails silently.
    private bool ToolCallingIsUnproven(AssistantRole role) =>
        Volatile.Read(ref _connections).For(role).Provider.Hosting is AssistantHosting.SelfHosted;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeSub?.Dispose();
        _narrateSub?.Dispose();
        foreach (var narration in _narrations.Values) narration.Dispose();
        _narrations.Clear();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        foreach (var action in _commitMessages.Values) action.Dispose();
        _commitMessages.Clear();
        _active.Dispose();
        _activeCommitMessage.Dispose();
        foreach (var configured in _isConfigured.Values) configured.Dispose();
        _keys.Dispose();
    }
}
