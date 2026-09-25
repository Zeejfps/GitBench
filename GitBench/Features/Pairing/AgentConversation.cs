using GitBench.Features.Editor;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>What the conversation's agent is doing, apart from any pairing session.</summary>
internal abstract record AgentPhase
{
    /// <summary>The agent is being started.</summary>
    public sealed record Starting : AgentPhase;

    /// <summary>The agent is up. <paramref name="Busy"/> while a turn is in flight.</summary>
    public sealed record Running(bool Busy) : AgentPhase;

    /// <summary>The agent could not be started, exited, or its terminal closed.</summary>
    public sealed record Gone(string Reason) : AgentPhase;
}

/// <summary>A turn for the agent: prose, and code the user sent along with it.</summary>
internal sealed record AgentPrompt(string Text, CodeQuote? Quote = null)
{
    /// <summary>The whole turn as markdown, for an agent that takes prose alone.</summary>
    public string ToMarkdown(string repoPath) =>
        Quote is { } quote ? Text + "\n\n" + quote.ToMarkdown(path => RepoRelative(repoPath, path)) : Text;

    /// <summary>A path as the agent is told it: relative to the repository, or as it is outside it.</summary>
    public static string RepoRelative(string repoPath, string absolutePath)
    {
        var relative = Path.GetRelativePath(repoPath, absolutePath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? absolutePath
            : relative.Replace('\\', '/');
    }
}

/// <summary>What a new conversation opens with.</summary>
internal abstract record AgentOpening
{
    /// <summary>A pairing session on this goal.</summary>
    public sealed record Pairing(string Goal) : AgentOpening;

    /// <summary>Something the user asked, with code they sent along.</summary>
    public sealed record Chat(string Text, CodeQuote? Quote) : AgentOpening;

    /// <summary>Nothing yet: the agent starts and waits for the user's first words.</summary>
    public sealed record Blank : AgentOpening;
}

/// <summary>Runs a conversation's agent.</summary>
internal interface IAgentDriver : IAsyncDisposable
{
    /// <summary>Gives the agent a turn: something the user said, or that a session began. Queued
    /// behind a turn already in flight. UI thread.</summary>
    void Tell(AgentPrompt prompt);
}

/// <summary>
/// A repository's agent and what the user and it said, across pairing sessions and between them:
/// once a session ends the agent stays, to be asked for a commit or a push, or to start the next
/// session. UI thread only.
/// </summary>
internal sealed class AgentConversation : IAsyncDisposable
{
    private readonly Func<string, AgentTranscript, Action<PairingAction>, PairingSession> _newSession;
    private readonly IUiDispatcher _dispatcher;
    private readonly State<AgentPhase> _phase = new(new AgentPhase.Starting());
    private readonly State<PairingSession?> _session = new(null);
    private readonly Derived<bool> _isComposing;
    private readonly Derived<bool> _takesChat;
    private IAgentDriver? _driver;
    private IDisposable? _sessionWatch;
    private bool _toldOpening = true;
    private bool _disposed;

    public AgentConversation(Repo repo, PairingHarness harness, Func<string, AgentTranscript, Action<PairingAction>, PairingSession> newSession, IUiDispatcher dispatcher)
    {
        Repo = repo;
        Harness = harness;
        _newSession = newSession;
        _dispatcher = dispatcher;
        _isComposing = new Derived<bool>(() => Transcript.OpenNarration.Value is null
            && (_session.Value is { } session
                ? session.Store.Phase.Value is PairingPhase.Starting or PairingPhase.Running { Waiting: false }
                : _phase.Value is AgentPhase.Starting or AgentPhase.Running { Busy: true }));
        _takesChat = new Derived<bool>(() => !IsGone && (IsPairing || Harness is PairingHarness.Acp));
    }

    public Repo Repo { get; }

    public PairingHarness Harness { get; }

    public AgentTranscript Transcript { get; } = new();

    public IReadable<AgentPhase> Phase => _phase;

    /// <summary>The pairing session running in the conversation, if one is.</summary>
    public IReadable<PairingSession?> Session => _session;

    /// <summary>True while the agent is at work and nothing it writes has landed yet.</summary>
    public IReadable<bool> IsComposing => _isComposing;

    public bool IsPairing => _session.Value?.Store.IsLive == true;

    public bool IsGone => _phase.Value is AgentPhase.Gone;

    /// <summary>Whether what the user types in the panel reaches the agent: always during a session,
    /// as the agent's next turn; between sessions only for an agent the app talks to itself.</summary>
    public IReadable<bool> TakesChat => _takesChat;

    public void Attach(IAgentDriver driver) => _driver = driver;

    /// <summary>For a conversation opened blank: what the agent is told of DiffDino goes with the
    /// user's first words, or with the first session they start.</summary>
    public void OpenOnFirstWords() => _toldOpening = false;

    /// <summary>Opens a pairing session in the conversation. Only while none is live.</summary>
    public PairingSession StartSession(string goal)
    {
        if (IsPairing) throw new InvalidOperationException("A pairing session is already running.");
        Transcript.Add(new PairingMessage.SessionStarted(goal));
        var session = _newSession(goal, Transcript, Deliver);
        _sessionWatch?.Dispose();
        _session.Value = session;
        if (_phase.Value is AgentPhase.Running) session.Store.MarkRunning();
        // Posted: the store tells the agent the session ended after the phase changes, and must still be whole then.
        _sessionWatch = session.Store.Phase.Subscribe(_ =>
        {
            if (!session.Store.IsLive) _dispatcher.Post(() => Retire(session));
        });
        return session;
    }

    // What the user did in the session reaches the agent as its next turn, queued behind one in flight.
    private void Deliver(PairingAction action) =>
        _driver?.Tell(new AgentPrompt(PairingInstructions.Move(action, path => AgentPrompt.RepoRelative(Repo.Path, path))));

    /// <summary>The user starts a session in a conversation already under way: the agent is told.</summary>
    public PairingSession BeginSession(string goal)
    {
        var session = StartSession(goal);
        _driver?.Tell(new AgentPrompt(_toldOpening
            ? PairingInstructions.Resumed(goal, Repo.Path)
            : PairingInstructions.Opening(goal, Repo.Path)));
        _toldOpening = true;
        return session;
    }

    private void Retire(PairingSession session)
    {
        if (_disposed || !ReferenceEquals(_session.Value, session)) return;
        _sessionWatch?.Dispose();
        _sessionWatch = null;
        Transcript.Add(new PairingMessage.SessionOver(session.Store.Phase.Value));
        _session.Value = null;
        session.Dispose();
    }

    /// <summary>Something the user said, with code they sent along: to the session's loop while one
    /// runs, else straight to the agent.</summary>
    public void Say(string text, CodeQuote? quote = null)
    {
        // Not gated on TakesChat: an agent in a terminal takes what is sent from the editor too.
        if (_disposed || string.IsNullOrWhiteSpace(text) || IsGone) return;
        if (_session.Value is { Store.IsLive: true } session)
        {
            session.Store.Say(text, quote);
            return;
        }

        var said = text.Trim();
        Transcript.AddFromUser(said, quote);
        _driver?.Tell(new AgentPrompt(_toldOpening ? said : PairingInstructions.ChatOpening(Repo.Path) + "\n\n" + said, quote));
        _toldOpening = true;
    }

    // ── the driver's side ────────────────────────────────────────────────────────────────────

    public void MarkRunning()
    {
        if (_phase.Value is not AgentPhase.Starting) return;
        _phase.Value = new AgentPhase.Running(false);
        _session.Value?.Store.MarkRunning();
    }

    public void BeginTurn()
    {
        Transcript.BeginAgentTurn();
        if (_phase.Value is AgentPhase.Running) _phase.Value = new AgentPhase.Running(true);
        _session.Value?.Store.MarkTurnStarted();
    }

    public void EndTurn()
    {
        Transcript.CloseNarration();
        if (_phase.Value is AgentPhase.Running) _phase.Value = new AgentPhase.Running(false);
        _session.Value?.Store.MarkTurnEnded();
    }

    /// <summary>The agent could not be started, or died; a live session fails with it.</summary>
    public void Fail(string reason)
    {
        if (!Lose(reason)) return;
        if (_session.Value is { Store.IsLive: true } session) session.Store.Fail(reason);
        else Transcript.AddNotice(reason, NoticeTone.Error);
    }

    /// <summary>The agent went away without failing, as a terminal that was closed does.</summary>
    public void Disconnect(string reason)
    {
        if (!Lose(reason)) return;
        if (_session.Value is { Store.IsLive: true } session) session.Store.MarkDisconnected(reason);
        else Transcript.AddNotice(reason, NoticeTone.Info);
    }

    private bool Lose(string reason)
    {
        if (_disposed || _phase.Value is AgentPhase.Gone) return false;
        Transcript.CloseNarration();
        _phase.Value = new AgentPhase.Gone(reason);
        return true;
    }

    /// <summary>Ends the session, if one runs, and stops the agent. Called on the UI thread, where
    /// the session is let go before anything is awaited.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionWatch?.Dispose();
        _session.Value?.Dispose();
        _session.Value = null;
        _isComposing.Dispose();
        _takesChat.Dispose();
        if (_driver is not null) await _driver.DisposeAsync().ConfigureAwait(false);
    }
}
