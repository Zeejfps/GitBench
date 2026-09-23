using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.CodeIntel;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Features.Repos;
using GitBench.Features.Terminal;
using GitBench.App;
using GitBench.Git;
using GitBench.Lsp.Lifecycle;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>Who drives a pairing session.</summary>
internal abstract record PairingHarness
{
    public abstract string Label { get; }

    /// <summary>An agent the app runs over ACP, with the write guard enforced by the app.</summary>
    public sealed record Acp(AcpHarness Harness) : PairingHarness
    {
        public override string Label => Harness.Label;
    }

    /// <summary>An agent CLI started in a terminal tab from a command template. Nothing the app
    /// does refuses its writes: that is left to the template's own flags.</summary>
    public sealed record Terminal(string Name, string Template) : PairingHarness
    {
        public override string Label => Name;
    }
}

/// <summary>One pairing session: the repository, the loop, and whatever runs the agent.</summary>
internal sealed class PairingSession : IAsyncDisposable
{
    private readonly IAsyncDisposable? _driver;
    private readonly IDisposable _presentation;

    public PairingSession(Repo repo, PairingHarness harness, PairingStore store, IDisposable presentation, IAsyncDisposable? driver)
    {
        Repo = repo;
        Harness = harness;
        Store = store;
        _presentation = presentation;
        _driver = driver;
    }

    public Repo Repo { get; }

    public PairingHarness Harness { get; }

    public PairingStore Store { get; }

    /// <summary>Ends the session. Called on the UI thread, where the store and the presentation are
    /// let go before anything is awaited; the driver's own teardown may finish elsewhere.</summary>
    public async ValueTask DisposeAsync()
    {
        Store.EndByUser();
        _presentation.Dispose();
        Store.Dispose();
        if (_driver is not null) await _driver.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>How starting a session came out.</summary>
internal abstract record PairingStart
{
    public sealed record Started(PairingSession Session) : PairingStart;

    /// <summary>A live session already runs for the repository.</summary>
    public sealed record AlreadyRunning(PairingSession Session) : PairingStart;
}

/// <summary>
/// Every repository's pairing session, one at most per repository, and the one for the repository
/// on screen. A finished session stays until the user closes it or starts another, so its summary
/// can be read. UI thread only.
/// </summary>
internal sealed class PairingSessions : IPairingSessions, IDisposable
{
    private readonly IRepoRegistry _repos;
    private readonly Func<Repo, string, PairingHarness, HintLevel, PairingSession> _create;
    private readonly Dictionary<Guid, PairingSession> _sessions = new();
    private readonly State<PairingSession?> _active = new(null);
    private readonly IDisposable _following;

    public PairingSessions(IRepoRegistry repos, Func<Repo, string, PairingHarness, HintLevel, PairingSession> create)
    {
        _repos = repos;
        _create = create;
        _following = repos.Active.Subscribe(_ => Refresh());
    }

    /// <summary>The session of the repository on screen, if it has one.</summary>
    public IReadable<PairingSession?> Active => _active;

    public PairingStore? StoreFor(Guid repoId) => _sessions.TryGetValue(repoId, out var session) ? session.Store : null;

    public PairingStart Start(Repo repo, string goal, PairingHarness harness, HintLevel startingHint)
    {
        if (_sessions.TryGetValue(repo.Id, out var existing))
        {
            if (existing.Store.IsLive) return new PairingStart.AlreadyRunning(existing);
            _sessions.Remove(repo.Id);
            _ = existing.DisposeAsync().AsTask();
        }

        var session = _create(repo, goal.Trim(), harness, startingHint);
        _sessions[repo.Id] = session;
        Refresh();
        return new PairingStart.Started(session);
    }

    /// <summary>Ends a repository's session, if it is still running, and takes it off screen.</summary>
    public void Close(Guid repoId)
    {
        if (!_sessions.Remove(repoId, out var session)) return;
        _ = session.DisposeAsync().AsTask();
        Refresh();
    }

    private void Refresh()
    {
        var active = _repos.Active.Value is { } repo && _sessions.TryGetValue(repo.Id, out var session) ? session : null;
        if (!ReferenceEquals(_active.Value, active)) _active.Value = active;
    }

    public void Dispose()
    {
        _following.Dispose();
        foreach (var session in _sessions.Values) _ = session.DisposeAsync().AsTask();
        _sessions.Clear();
    }

    /// <summary>The factory the app runs sessions with: the Files pane as the surface, git for the
    /// snapshots, and the harness's own driver.</summary>
    public static Func<Repo, string, PairingHarness, HintLevel, PairingSession> Factory(
        IRepoRegistry repos,
        IFileBrowserStore browsers,
        IFileTextSource texts,
        ISymbolExtractor extractor,
        RepoDocumentSaver saver,
        WorkingTreeSnapshots snapshots,
        PairingTestCommands testCommands,
        AgentEndpoints endpoints,
        IServerEnvironment environment,
        ITerminalSessionStore terminals,
        CommandLaunchFactory launches,
        IContentNavigator navigator,
        IUiDispatcher dispatcher,
        TimeProvider clock) =>
        (repo, goal, harness, startingHint) =>
        {
            var presentation = new EditorPairingPresentation(repo, repos, browsers, texts, extractor, saver);
            var store = new PairingStore(goal, harness.Label, presentation, new GitPairingWorkspace(repo.Path, snapshots, testCommands), dispatcher, clock, startingHint);
            IAsyncDisposable driver = harness switch
            {
                PairingHarness.Acp acp => AcpPairingDriver.Start(store, repo, acp.Harness, endpoints, environment, dispatcher),
                PairingHarness.Terminal terminal => TerminalPairingDriver.Start(
                    store, repo, terminal.Name, terminal.Template, endpoints, terminals, launches, navigator, dispatcher),
                _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "Unknown harness."),
            };
            return new PairingSession(repo, harness, store, presentation, driver);
        };
}
