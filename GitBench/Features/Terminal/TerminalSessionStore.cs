using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// The one place terminals live: one per repository, in memory, for the app session.
/// </summary>
internal interface ITerminalSessionStore
{
    /// <summary>The active repository's terminal, or null when no repository is active. Swaps on
    /// repo switch, so the pane binds to this and never asks which repo it is showing.</summary>
    IReadable<TerminalInstance?> Active { get; }

    /// <summary>Whether this repository's terminal is holding a shell process.</summary>
    bool HasLiveShell(Guid repoId);

    /// <summary>
    /// Every repository holding a shell process, for warning about what closing would end.
    /// </summary>
    /// <remarks>
    /// Unordered: a caller showing these to someone should put them in an order that reader
    /// recognises, which is the registry's, not this dictionary's.
    /// </remarks>
    IReadOnlyList<Guid> ReposWithLiveShells();
}

/// <summary>
/// Owns every repository's terminal, keyed by repo id, so switching away from a repo and back
/// returns to the same shell — with its scrollback, and still running.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Assistant.AssistantSessionStore"/>'s shape: per-repo state, an "active"
/// projection that swaps on repo switch, and a <see cref="Start"/> that wires the registry once the
/// UI loop exists. Terminals are session-only and never persisted — a shell is a process, and a
/// process does not survive the application that started it.
/// </para>
/// <para>
/// A terminal is made when a repository is first activated, but making one starts nothing: a fresh
/// instance is idle and holds no process until something asks it for a shell. That keeps "only a
/// click starts a shell" in one place rather than splitting it between this and whatever asks.
/// </para>
/// </remarks>
internal sealed class TerminalSessionStore : ITerminalSessionStore, IHostedService, IDisposable
{
    readonly IRepoRegistry _registry;
    readonly IPtySessionFactory _ptys;
    readonly ITerminalEngineFactory _engines;
    readonly IUiDispatcher _dispatcher;
    readonly IClipboard? _clipboard;
    readonly TerminalLaunchFactory _launches;

    readonly Dictionary<Guid, TerminalInstance> _instances = new();
    readonly State<TerminalInstance?> _active = new(null);

    IDisposable? _activeSub;
    IDisposable? _reposSub;
    bool _started;
    bool _disposed;

    public TerminalSessionStore(
        IRepoRegistry registry,
        IPtySessionFactory ptys,
        ITerminalEngineFactory engines,
        IUiDispatcher dispatcher,
        TerminalLaunchFactory? launches = null,
        IClipboard? clipboard = null)
    {
        _registry = registry;
        _ptys = ptys;
        _engines = engines;
        _dispatcher = dispatcher;
        _clipboard = clipboard;
        _launches = launches ?? DefaultLaunch;
    }

    public IReadable<TerminalInstance?> Active => _active;

    public bool HasLiveShell(Guid repoId) =>
        _instances.TryGetValue(repoId, out var instance) && instance.HasLiveShell;

    public IReadOnlyList<Guid> ReposWithLiveShells() =>
        _instances.Where(pair => pair.Value.HasLiveShell).Select(pair => pair.Key).ToArray();

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;

        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());

        // Every change rather than the removals alone: a list says it was cleared or reset without
        // saying what left, so what is gone is read off the list itself. A repository that is no
        // longer open has no working tree for a shell to sit in, and reconciliation removes rows
        // the user never touched — a pruned worktree takes its terminal with it.
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
    }

    void OnActiveChanged()
    {
        if (_disposed) return;

        _active.Value = _registry.Active.Value is { } repo ? InstanceFor(repo) : null;
    }

    TerminalInstance InstanceFor(Repo repo)
    {
        if (_instances.TryGetValue(repo.Id, out var existing)) return existing;

        var instance = new TerminalInstance(_launches(repo), _dispatcher);
        _instances[repo.Id] = instance;
        return instance;
    }

    void DropClosedRepos()
    {
        if (_disposed || _instances.Count == 0) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        var closed = _instances.Keys.Where(id => !open.Contains(id)).ToArray();

        foreach (var id in closed)
        {
            var instance = _instances[id];
            _instances.Remove(id);

            // The pane is showing it, and is about to be shown a repository that no longer exists
            // either. Cleared before disposal so nothing draws a screen whose session has gone.
            if (ReferenceEquals(_active.Value, instance)) _active.Value = null;

            instance.Dispose();
        }
    }

    ITerminalLaunch DefaultLaunch(Repo repo) => new ShellLaunch(repo.Path, _ptys, _engines, _clipboard);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _activeSub?.Dispose();
        _reposSub?.Dispose();

        // The pane may still be mounted: the application disposes its services before it unmounts
        // its view tree. Nothing may be pointing at a terminal whose shell is being killed.
        _active.Value = null;

        foreach (var instance in _instances.Values) instance.Dispose();
        _instances.Clear();
        _active.Dispose();
    }
}

/// <summary>What a repository's terminal runs. Substituted in tests, which have no shell to spawn.</summary>
internal delegate ITerminalLaunch TerminalLaunchFactory(Repo repo);
