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

/// <summary>One pairing session: the repository, the loop, and the editor it shows in. The agent
/// that drives it belongs to the conversation the session runs in.</summary>
internal sealed class PairingSession : IDisposable
{
    private readonly IDisposable _presentation;

    public PairingSession(Repo repo, PairingStore store, IDisposable presentation)
    {
        Repo = repo;
        Store = store;
        _presentation = presentation;
    }

    public Repo Repo { get; }

    public PairingStore Store { get; }

    /// <summary>Ends the session. UI thread.</summary>
    public void Dispose()
    {
        Store.EndByUser();
        _presentation.Dispose();
        Store.Dispose();
    }
}

/// <summary>How the user starting a session came out.</summary>
internal abstract record PairingStart
{
    public sealed record Started(AgentConversation Conversation) : PairingStart;

    /// <summary>A live session already runs for the repository.</summary>
    public sealed record AlreadyRunning(AgentConversation Conversation) : PairingStart;
}

/// <summary>How the agent starting a session came out.</summary>
internal abstract record AgentPairingStart
{
    public sealed record Started(PairingStore Store) : AgentPairingStart;

    /// <summary>A live session already runs for the repository.</summary>
    public sealed record AlreadyRunning(PairingStore Store) : AgentPairingStart;

    /// <summary>No conversation with an agent is open for the repository in DiffDino.</summary>
    public sealed record NoConversation : AgentPairingStart;
}

/// <summary>
/// Every repository's agent conversation, one at most per repository, and the one for the
/// repository on screen. A conversation stays until the user closes it, across the pairing
/// sessions run in it. UI thread only.
/// </summary>
internal sealed class PairingSessions : IPairingSessions, IDisposable
{
    private readonly IRepoRegistry _repos;
    private readonly Func<Repo, PairingHarness, AgentOpening, AgentConversation> _create;
    private readonly Dictionary<Guid, AgentConversation> _conversations = new();
    private readonly State<AgentConversation?> _active = new(null);
    private readonly IDisposable _following;

    public PairingSessions(IRepoRegistry repos, Func<Repo, PairingHarness, AgentOpening, AgentConversation> create)
    {
        _repos = repos;
        _create = create;
        _following = repos.Active.Subscribe(_ => Refresh());
    }

    /// <summary>The conversation of the repository on screen, if it has one.</summary>
    public IReadable<AgentConversation?> Active => _active;

    /// <summary>The repository's conversation while its agent is still there.</summary>
    public AgentConversation? LiveConversation(Guid repoId) =>
        _conversations.TryGetValue(repoId, out var conversation) && !conversation.IsGone ? conversation : null;

    public PairingStore? StoreFor(Guid repoId) =>
        _conversations.TryGetValue(repoId, out var conversation) ? conversation.Session.Value?.Store : null;

    /// <summary>The user starts a session: in the repository's conversation while its agent is
    /// there, which keeps the agent it has, or in a new one with <paramref name="harness"/>.</summary>
    public PairingStart Start(Repo repo, string goal, PairingHarness harness)
    {
        if (LiveConversation(repo.Id) is { } existing)
        {
            if (existing.IsPairing) return new PairingStart.AlreadyRunning(existing);
            existing.BeginSession(goal.Trim());
            return new PairingStart.Started(existing);
        }

        return new PairingStart.Started(Open(repo, harness, new AgentOpening.Pairing(goal.Trim())));
    }

    /// <summary>The user asks the agent something, with code they sent along: in the repository's
    /// conversation while its agent is there, or in a new one with <paramref name="harness"/>.</summary>
    public AgentConversation Ask(Repo repo, string text, CodeQuote? quote, PairingHarness harness)
    {
        if (LiveConversation(repo.Id) is { } existing)
        {
            existing.Say(text, quote);
            return existing;
        }

        return Open(repo, harness, new AgentOpening.Chat(text.Trim(), quote));
    }

    private AgentConversation Open(Repo repo, PairingHarness harness, AgentOpening opening)
    {
        if (_conversations.Remove(repo.Id, out var gone)) _ = gone.DisposeAsync().AsTask();
        var conversation = _create(repo, harness, opening);
        _conversations[repo.Id] = conversation;
        Refresh();
        return conversation;
    }

    public AgentPairingStart StartByAgent(Guid repoId, string goal)
    {
        if (LiveConversation(repoId) is not { } conversation) return new AgentPairingStart.NoConversation();
        if (conversation.Session.Value is { Store.IsLive: true } running) return new AgentPairingStart.AlreadyRunning(running.Store);
        return new AgentPairingStart.Started(conversation.StartSession(goal.Trim()).Store);
    }

    /// <summary>Ends a repository's conversation — its session, if one runs, and its agent — and
    /// takes it off screen.</summary>
    public void Close(Guid repoId)
    {
        if (!_conversations.Remove(repoId, out var conversation)) return;
        _ = conversation.DisposeAsync().AsTask();
        Refresh();
    }

    private void Refresh()
    {
        var active = _repos.Active.Value is { } repo && _conversations.TryGetValue(repo.Id, out var conversation) ? conversation : null;
        if (!ReferenceEquals(_active.Value, active)) _active.Value = active;
    }

    public void Dispose()
    {
        _following.Dispose();
        foreach (var conversation in _conversations.Values) _ = conversation.DisposeAsync().AsTask();
        _conversations.Clear();
    }

    /// <summary>The factory the app runs conversations with: the Files pane as the surface of their
    /// sessions, git for the snapshots, and the harness's own driver, opened on the first goal.</summary>
    public static Func<Repo, PairingHarness, AgentOpening, AgentConversation> Factory(
        IRepoRegistry repos,
        IFileBrowserStore browsers,
        IFileTextSource texts,
        ISymbolExtractor extractor,
        RepoDocumentSaver saver,
        WorkingTreeSnapshots snapshots,
        AgentEndpoints endpoints,
        IServerEnvironment environment,
        ITerminalSessionStore terminals,
        CommandLaunchFactory launches,
        IContentNavigator navigator,
        IUiDispatcher dispatcher,
        TimeProvider clock) =>
        (repo, harness, opening) =>
        {
            var conversation = new AgentConversation(repo, harness, (sessionGoal, transcript) =>
            {
                var presentation = new EditorPairingPresentation(repo, repos, browsers, texts, extractor, saver);
                var store = new PairingStore(
                    sessionGoal, harness.Label, transcript, presentation, new GitPairingWorkspace(repo.Path, snapshots), dispatcher, clock);
                return new PairingSession(repo, store, presentation);
            }, dispatcher);
            AgentPrompt first;
            switch (opening)
            {
                case AgentOpening.Pairing pairing:
                    conversation.StartSession(pairing.Goal);
                    first = new AgentPrompt(PairingInstructions.Opening(pairing.Goal, repo.Path));
                    break;
                case AgentOpening.Chat chat:
                    conversation.Transcript.AddFromUser(chat.Text, chat.Quote);
                    first = new AgentPrompt(PairingInstructions.ChatOpening(repo.Path) + "\n\n" + chat.Text, chat.Quote);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(opening), opening, "Unknown opening.");
            }

            IAgentDriver driver = harness switch
            {
                PairingHarness.Acp acp => AcpPairingDriver.Start(conversation, first, acp.Harness, endpoints, environment, dispatcher),
                PairingHarness.Terminal terminal => TerminalPairingDriver.Start(
                    conversation, first, terminal.Name, terminal.Template, endpoints, terminals, launches, navigator, dispatcher),
                _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "Unknown harness."),
            };
            conversation.Attach(driver);
            return conversation;
        };
}
