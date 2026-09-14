using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.Features.AgentConnections;

/// <summary>
/// One connected agent's footprint in the app: the repository it last drove a review window in
/// (its default when a call names none), the walkthrough stores it has narrated, and a watch on
/// its presence — an agent that stops calling back after a walkthrough returned is treated as gone,
/// since a client that vanishes without ending its session sends nothing. Every member is
/// UI-thread only; the source posts here.
/// </summary>
internal sealed class AgentSession
{
    /// <summary>How long a narrated walkthrough waits for the agent's next walkthrough call before
    /// the rail is told the agent went away.</summary>
    public static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(120);

    private readonly McpSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly HashSet<ReviewWalkthroughStore> _narrated = new(ReferenceEqualityComparer.Instance);

    private RepoDefault _default = new RepoDefault.None();
    private Presence _presence = new Presence.Unwatched();
    private int _presenceGeneration;
    private bool _ended;

    public AgentSession(McpSession session, IUiDispatcher dispatcher, TimeProvider clock)
    {
        _session = session;
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public string Id => _session.Id;

    /// <summary>Fires when the client ends the session or the server stops.</summary>
    public CancellationToken Ended => _session.Ended;

    public RepoDefault Default => _default;

    /// <summary>A presentation or walkthrough call is about to run against this repository.</summary>
    public void NoteDriving(Repo repo) => _default = new RepoDefault.LastDriven(repo);

    /// <summary>A walkthrough call is about to run against this store: the agent is present, so
    /// any watch on it is dropped, and the store is remembered for the session's end.</summary>
    public void NoteNarrating(ReviewWalkthroughStore store)
    {
        _narrated.Add(store);
        Disarm();
    }

    /// <summary>A walkthrough call has returned. While the store is still being narrated the agent
    /// owes it another call, so the presence watch starts; a store that is idle or already
    /// abandoned needs none.</summary>
    public void WatchPresence(ReviewWalkthroughStore store)
    {
        if (_ended) return;
        Disarm();
        if (store.Phase.Value is not WalkthroughPhase.Narrating) return;

        var generation = ++_presenceGeneration;
        var timer = _clock.CreateTimer(
            _ => _dispatcher.Post(() => OnPresenceTimeout(generation)),
            null,
            PresenceTimeout,
            Timeout.InfiniteTimeSpan);
        _presence = new Presence.Watching(store, timer, generation);
    }

    /// <summary>The session is over: every walkthrough it narrated is told so, once.</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        Disarm();
        foreach (var store in _narrated) store.MarkDisconnected();
        _narrated.Clear();
    }

    private void OnPresenceTimeout(int generation)
    {
        if (_presence is not Presence.Watching { Generation: var armed } watching || armed != generation) return;
        _presence = new Presence.Unwatched();
        watching.Timer.Dispose();
        watching.Store.MarkDisconnected();
    }

    private void Disarm()
    {
        if (_presence is not Presence.Watching watching) return;
        watching.Timer.Dispose();
        _presence = new Presence.Unwatched();
    }

    private abstract record Presence
    {
        public sealed record Unwatched : Presence;

        public sealed record Watching(ReviewWalkthroughStore Store, ITimer Timer, int Generation) : Presence;
    }
}
