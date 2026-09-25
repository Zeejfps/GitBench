using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
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

    /// <summary>An agent the app runs over ACP from a preset, with the write guard enforced by the
    /// app.</summary>
    public sealed record Acp(AgentPreset Preset) : PairingHarness
    {
        public override string Label => Preset.Name;
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
/// sessions run in it; hiding its panel leaves it running. UI thread only.
/// </summary>
internal sealed class PairingSessions : IPairingSessions, IDisposable
{
    private readonly IRepoRegistry _repos;
    private readonly Func<Repo, PairingHarness, AgentOpening, AgentConversation> _create;
    private readonly Dictionary<Guid, AgentConversation> _conversations = new();
    private readonly HashSet<Guid> _hidden = new();
    private readonly State<AgentConversation?> _active = new(null);
    private readonly State<AgentConversation?> _shown = new(null);
    private readonly IDisposable _following;

    public PairingSessions(IRepoRegistry repos, Func<Repo, PairingHarness, AgentOpening, AgentConversation> create)
    {
        _repos = repos;
        _create = create;
        _following = repos.Active.Subscribe(_ => Refresh());
    }

    /// <summary>The conversation of the repository on screen, if it has one.</summary>
    public IReadable<AgentConversation?> Active => _active;

    /// <summary>The conversation of the repository on screen while its panel is shown.</summary>
    public IReadable<AgentConversation?> Shown => _shown;

    /// <summary>The repository's conversation, its agent there or gone.</summary>
    public AgentConversation? ConversationOf(Guid repoId) => _conversations.GetValueOrDefault(repoId);

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
            ShowPanel(repo.Id);
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
            ShowPanel(repo.Id);
            existing.Say(text, quote);
            return existing;
        }

        return Open(repo, harness, new AgentOpening.Chat(text.Trim(), quote));
    }

    /// <summary>A new conversation with <paramref name="preset"/> that waits for the user to say
    /// something, in place of the repository's conversation if it has one.</summary>
    public AgentConversation OpenChat(Repo repo, AgentPreset preset) =>
        Open(repo, new PairingHarness.Acp(preset), new AgentOpening.Blank());

    /// <summary>Whether the repository's conversation can start over: an ACP agent with no pairing
    /// session running.</summary>
    public static bool CanRestart(AgentConversation conversation) =>
        conversation.Harness is PairingHarness.Acp && !conversation.IsPairing;

    /// <summary>A fresh conversation with the same agent in place of the repository's, so the agent
    /// starts again with none of what was said.</summary>
    public AgentConversation? Restart(Guid repoId) =>
        _conversations.TryGetValue(repoId, out var conversation)
        && CanRestart(conversation)
        && conversation.Harness is PairingHarness.Acp acp
            ? OpenChat(conversation.Repo, acp.Preset)
            : null;

    private AgentConversation Open(Repo repo, PairingHarness harness, AgentOpening opening)
    {
        if (_conversations.Remove(repo.Id, out var gone)) _ = gone.DisposeAsync().AsTask();
        var conversation = _create(repo, harness, opening);
        _conversations[repo.Id] = conversation;
        _hidden.Remove(repo.Id);
        Refresh();
        return conversation;
    }

    public AgentPairingStart StartByAgent(Guid repoId, string goal)
    {
        if (LiveConversation(repoId) is not { } conversation) return new AgentPairingStart.NoConversation();
        if (conversation.Session.Value is { Store.IsLive: true } running) return new AgentPairingStart.AlreadyRunning(running.Store);
        ShowPanel(repoId);
        return new AgentPairingStart.Started(conversation.StartSession(goal.Trim()).Store);
    }

    /// <summary>Puts the repository's conversation back on screen.</summary>
    public void ShowPanel(Guid repoId)
    {
        if (_hidden.Remove(repoId)) Refresh();
    }

    /// <summary>Takes the repository's conversation off screen; its agent keeps running.</summary>
    public void HidePanel(Guid repoId)
    {
        if (_conversations.ContainsKey(repoId) && _hidden.Add(repoId)) Refresh();
    }

    public bool IsPanelShown(Guid repoId) => _conversations.ContainsKey(repoId) && !_hidden.Contains(repoId);

    /// <summary>Ends a repository's conversation — its session, if one runs, and its agent — and
    /// takes it off screen.</summary>
    public void Close(Guid repoId)
    {
        if (!_conversations.Remove(repoId, out var conversation)) return;
        _hidden.Remove(repoId);
        _ = conversation.DisposeAsync().AsTask();
        Refresh();
    }

    private void Refresh()
    {
        var active = _repos.Active.Value is { } repo && _conversations.TryGetValue(repo.Id, out var conversation) ? conversation : null;
        if (!ReferenceEquals(_active.Value, active)) _active.Value = active;
        var shown = active is not null && !_hidden.Contains(active.Repo.Id) ? active : null;
        if (!ReferenceEquals(_shown.Value, shown)) _shown.Value = shown;
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
        ISyntaxHighlighter highlighter,
        IDraftDefinitionSource servers,
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
                var presentation = new EditorPairingPresentation(repo, repos, browsers, texts, extractor, saver, highlighter, servers, dispatcher);
                var store = new PairingStore(
                    sessionGoal, harness.Label, transcript, presentation, new GitPairingWorkspace(repo.Path, snapshots), dispatcher, clock);
                return new PairingSession(repo, store, presentation);
            }, dispatcher);
            AgentPrompt? first;
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
                case AgentOpening.Blank:
                    conversation.OpenOnFirstWords();
                    first = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(opening), opening, "Unknown opening.");
            }

            IAgentDriver driver = harness switch
            {
                PairingHarness.Acp acp => AcpPairingDriver.Start(conversation, first, acp.Preset, endpoints, environment, dispatcher),
                PairingHarness.Terminal terminal => TerminalPairingDriver.Start(
                    conversation, first ?? throw new InvalidOperationException("A terminal agent opens on a goal or a message."), terminal.Name, terminal.Template, endpoints, terminals, launches, navigator, dispatcher),
                _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "Unknown harness."),
            };
            conversation.Attach(driver);
            return conversation;
        };
}
