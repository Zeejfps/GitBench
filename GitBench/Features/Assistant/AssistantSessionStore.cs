using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Diff.Reading;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Builds the backend the sessions talk to. <paramref name="connection"/> is read per request, so a
/// provider, model or key changed after startup takes effect without rebuilding anything.
/// </summary>
internal delegate IAssistantBackend AssistantBackendFactory(Func<AssistantConnection> connection);

/// <summary>
/// The one place assistant conversations live: one per repository, in memory, for the app session.
/// View models project from it.
/// </summary>
internal interface IAssistantSessionStore : IReadingModeFactory
{
    /// <summary>The active repo's conversation, or null when no repo is active. Swaps on repo switch.</summary>
    IReadable<AssistantSession?> Active { get; }

    /// <summary>The active repo's "Generate commit message" action, or null when no repo is
    /// active.</summary>
    IReadable<CommitMessageQuickAction?> CommitMessage { get; }

    /// <summary>Which provider the assistant talks to, and the model and endpoint chosen for it.</summary>
    IReadable<AssistantSettings> Settings { get; }

    /// <summary>Whether the assistant can reach a model at all: a key resolved, or a provider that
    /// needs none.</summary>
    IReadable<bool> IsConfigured { get; }

    /// <summary>What every provider has for a key, so a card or a switcher can say which are ready
    /// and hold the right one — always asked for by provider, never for "the" key.</summary>
    IReadable<AssistantKeyring> Keys { get; }

    /// <summary>Points the assistant at a provider and settles its key: null leaves whatever is
    /// stored alone, empty forgets it, and anything else is saved. The connection points at the new
    /// provider before this returns, so nothing sent afterwards can reach the old one; the secret
    /// store is read and written off the UI thread, and <see cref="IsConfigured"/> stays down until
    /// a key that was not already known has landed.</summary>
    void Save(AssistantSettings settings, string? apiKey);

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
/// </remarks>
internal sealed class AssistantSessionStore : IAssistantSessionStore, IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly AssistantCredentials _credentials;
    private readonly ILocalizationService _loc;
    private readonly IUiDispatcher _dispatcher;
    private readonly IReviewProgressStore _reviewProgress;
    private readonly AgentCatalog _catalog;
    private readonly AgentDefinition _agent;
    private readonly AgentDefinition _commitMessageAgent;
    private readonly IAssistantBackend _backend;
    private readonly AssistantWriteSurface _writes;

    private readonly Dictionary<Guid, AssistantSession> _sessions = new();
    private readonly Dictionary<Guid, CommitMessageQuickAction> _commitMessages = new();
    private readonly State<AssistantSession?> _active = new(null);
    private readonly State<CommitMessageQuickAction?> _activeCommitMessage = new(null);
    private readonly State<AssistantSettings> _settings;
    private readonly State<bool> _isConfigured = new(false);
    private readonly State<AssistantKeyring> _keys = new(AssistantKeyring.Empty);

    // Read by the backend from whatever thread a turn runs on; written only on the UI thread.
    private AssistantConnection _connection;

    // Counts the resolves asked for, so a slow one landing after a later one is dropped rather than
    // reinstating the provider and key it was asked about.
    private int _resolves;

    // Every resolve runs after the one before it has finished with the secret store. The counter
    // above orders the requests; this orders the reads and writes themselves, so a later pass's
    // read can never overtake an earlier pass's write and publish a keyring missing the key that
    // was just saved. Touched only on the UI thread, where every resolve is asked for.
    private Task _resolving = Task.CompletedTask;

    private IDisposable? _activeSub;
    private bool _started;
    private bool _disposed;

    public AssistantSessionStore(
        IRepoRegistry registry,
        IGitService git,
        AssistantCredentials credentials,
        State<AssistantSettings> settings,
        ILocalizationService loc,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ICommitEditor commitEditor,
        IReviewProgressStore reviewProgress,
        IRepoOperationsStore operations,
        AssistantBackendFactory backendFactory)
    {
        _registry = registry;
        _git = git;
        _credentials = credentials;
        _settings = settings;
        _loc = loc;
        _dispatcher = dispatcher;
        _reviewProgress = reviewProgress;
        _writes = new AssistantWriteSurface(dispatcher, bus, registry, commitEditor, operations);
        _connection = settings.Value.Connect(null);
        _catalog = AgentCatalog.LoadEmbedded();
        _agent = _catalog.Get(AgentCatalog.GeneralAgent);
        _commitMessageAgent = _catalog.Get(AgentCatalog.CommitMessageAgent);
        _backend = backendFactory(() => Volatile.Read(ref _connection));
    }

    public IReadable<AssistantSession?> Active => _active;

    public IReadable<CommitMessageQuickAction?> CommitMessage => _activeCommitMessage;

    public IReadable<AssistantSettings> Settings => _settings;

    public IReadable<bool> IsConfigured => _isConfigured;

    public IReadable<AssistantKeyring> Keys => _keys;

    /// <summary>
    /// Reading mode for one repository, or null when there is no such repository.
    /// </summary>
    /// <remarks>Whether a model can actually be reached is answered by the coordinator's
    /// <see cref="ReadingModeCoordinator.Available"/> rather than here, because credentials resolve
    /// asynchronously and a window opened before they land would otherwise never offer the toggle.
    ///
    /// The connection is read per run rather than captured, so switching provider or model between
    /// runs takes effect on the next one; the model id is also part of the plan cache key, so a
    /// switch produces a fresh plan rather than reusing the old model's.</remarks>
    public ReadingModeCoordinator? Create(Guid repoId)
    {
        var repo = _registry.Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is null) return null;

        return new ReadingModeCoordinator(repo, _git, _loc, _dispatcher, () => !_isConfigured.Value ? null : new DiffAbridger(
            _git,
            repo,
            _catalog,
            _backend,
            _loc,
            () => Volatile.Read(ref _connection).Provider.ModelFor(_catalog.Get(DiffAbridger.AgentName).Tier)),
            _isConfigured);
    }

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        Resolve(_settings.Value, save: null);
    }

    public void Save(AssistantSettings settings, string? apiKey)
    {
        var key = apiKey?.Trim();
        var previous = _settings.Value.ProviderId;

        // The connection moves first, and without an await in front of it. Everything below announces
        // the switch — the header names the new provider, the transcript says it was switched to —
        // while the secret store is milliseconds away at best and a locked keyring is seconds. A
        // request signed between the announcement and the answer has to be the new provider's.
        PointAt(settings, key);

        _settings.Value = settings;
        if (!string.Equals(previous, settings.ProviderId, StringComparison.Ordinal))
            RestartConversations(settings.Provider);
        Resolve(settings, key);
    }

    // What is already known about the chosen provider's key, with this save's edit applied: the
    // answer the resolve will come back with, minus whatever only the secret store can add. A
    // provider whose key is not known yet lowers IsConfigured for the duration, which is what closes
    // the composer and the presets rather than letting them reach the provider being left behind.
    private void PointAt(AssistantSettings settings, string? key)
    {
        var state = _keys.Value.For(settings.Provider);
        var edited = key switch
        {
            { Length: > 0 } => state with { SavedKey = key },
            { Length: 0 } => state with { SavedKey = null },
            _ => state,
        };
        Volatile.Write(ref _connection, settings.Connect(edited.ApiKey));
        _isConfigured.Value = edited.IsUsable;
    }

    // A tool-call id belongs to the provider that issued it — Anthropic's validator takes the id
    // literally and strict OpenAI-compatible endpoints refuse anything not in their own shape — and
    // the whole conversation is replayed on every turn. So the exchange the model is sent starts
    // again at the switch. The transcript is untouched: what was said is still what was said.
    private void RestartConversations(AssistantProvider provider)
    {
        var notice = _loc.Strings.Value.AssistantProviderSwitched(provider.DisplayName);
        foreach (var session in _sessions.Values)
            session.RestartForProviderChange(notice);
    }

    // Reads (and optionally rewrites) the secret store on a worker, then posts the result back. What
    // ends up in effect is re-read rather than assumed: the environment fallback can outrank a save.
    private void Resolve(AssistantSettings settings, string? save)
    {
        var credentials = _credentials;
        var dispatcher = _dispatcher;
        var provider = settings.Provider;
        var resolve = ++_resolves;
        _resolving = _resolving.ContinueWith(
            _ =>
            {
                var keys = AssistantKeyring.Empty;
                try
                {
                    if (save is { Length: > 0 }) credentials.Save(provider, save);
                    else if (save is not null) credentials.Clear(provider);
                    keys = credentials.Keyring();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Assistant] API key lookup failed: {ex.Message}");
                }

                // A secret store that refused the write still leaves the key in effect for this
                // session, and the card holds it: it is the app's own for as long as it is running.
                if (save is { Length: > 0 } && keys.For(provider).SavedKey is null)
                    keys = keys.With(provider, keys.For(provider) with { SavedKey = save });

                dispatcher.Post(() => Adopt(resolve, settings, keys));
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    // Confirms what Save already pointed the assistant at, now that the secret store has answered:
    // the connection is built here and in PointAt and nowhere else, from one settings and one
    // keyring, so the key that signs a request is by construction the key of the provider it is
    // sent to. A resolve overtaken by a later one is dropped rather than reinstating what it asked
    // about.
    private void Adopt(int resolve, AssistantSettings settings, AssistantKeyring keys)
    {
        if (_disposed || resolve != _resolves) return;
        var connection = settings.Connect(keys.For(settings.Provider).ApiKey);
        Volatile.Write(ref _connection, connection);
        _isConfigured.Value = connection.IsUsable;
        _keys.Value = keys;
    }

    // Built per run rather than kept: a preset is a one-shot, and its loop carries the state of the
    // exchange it drove. Nothing is written by these agents, so they get the reads only.
    public void RunPreset(string agentName, string prompt)
    {
        if (_disposed) return;

        // A preset carries a diff and the checkout's path to whichever provider is in effect, and it
        // has no composer to grey out, so it asks for the same gate the composer is given.
        if (!_isConfigured.Value) return;

        if (_registry.Active.Value is not { } repo) return;
        if (_active.Value is not { } session) return;

        var agent = _catalog.Get(agentName);
        var toolset = AssistantToolset.ForRepo(_git, repo, agent);
        session.RunPreset(prompt, new AssistantAgentLoop(_backend, agent, toolset, ToolCallingIsUnproven));
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
        var toolset = AssistantToolset.ForRepo(_git, repo, _agent, _reviewProgress, _writes);
        var session = new AssistantSession(
            repo, _git, new AssistantAgentLoop(_backend, _agent, toolset, ToolCallingIsUnproven), _loc, _dispatcher);
        _sessions[repo.Id] = session;
        return session;
    }

    // Keyed per repo like the conversations, so a generation started before a repo switch still
    // knows which checkout it was asked about when it comes back. The agent's allowed list is what
    // limits it: reads, plus the one write that fills the commit box it was asked to fill.
    private CommitMessageQuickAction CommitMessageFor(Repo repo)
    {
        if (_commitMessages.TryGetValue(repo.Id, out var existing)) return existing;

        var toolset = AssistantToolset.ForRepo(_git, repo, _commitMessageAgent, _reviewProgress, _writes);
        var action = new CommitMessageQuickAction(
            repo,
            _git,
            new AssistantAgentLoop(_backend, _commitMessageAgent, toolset, ToolCallingIsUnproven),
            _writes,
            _loc,
            _dispatcher);
        _commitMessages[repo.Id] = action;
        return action;
    }

    // Whether a turn that never calls a tool is worth reporting: against a self-hosted endpoint it
    // may mean the loaded model cannot call tools at all, which otherwise fails silently.
    private bool ToolCallingIsUnproven() => !Volatile.Read(ref _connection).Provider.ToolCalling;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeSub?.Dispose();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        foreach (var action in _commitMessages.Values) action.Dispose();
        _commitMessages.Clear();
        _active.Dispose();
        _activeCommitMessage.Dispose();
        _isConfigured.Dispose();
        _keys.Dispose();
    }
}
