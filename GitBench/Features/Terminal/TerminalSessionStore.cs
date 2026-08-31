using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// The one place terminals live: several per repository, in memory, for the app session.
/// </summary>
internal interface ITerminalSessionStore
{
    /// <summary>The active repository's terminals, or null when no repository is active. Swaps on
    /// repo switch, so the pane binds to this and never asks which repo it is showing.</summary>
    IReadable<TerminalTabs?> Tabs { get; }

    /// <summary>Whether any of this repository's terminals is holding a shell process.</summary>
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
/// Owns every repository's terminals, keyed by repo id, so switching away from a repo and back
/// returns to the same tabs — with the one that was in front still in front, its scrollback intact
/// and its shell still running.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Assistant.AssistantSessionStore"/>'s shape: per-repo state, an "active"
/// projection that swaps on repo switch, and a <see cref="Start"/> that wires the registry once the
/// UI loop exists. Terminals are session-only and never persisted — a shell is a process, and a
/// process does not survive the application that started it.
/// </para>
/// <para>
/// A repository's terminals are made when it is first activated, but making one starts nothing: a
/// fresh instance is idle and holds no process until something asks it for a shell. That keeps "only
/// a click starts a shell" in one place rather than splitting it between this and whatever asks.
/// </para>
/// </remarks>
internal sealed class TerminalSessionStore : ITerminalSessionStore, IHostedService, IDisposable
{
    readonly IRepoRegistry _registry;
    readonly IPtySessionFactory _ptys;
    readonly ITerminalEngineFactory _engines;
    readonly IUiDispatcher _dispatcher;
    readonly IClipboard? _clipboard;
    readonly ITerminalPalette? _palette;
    readonly TerminalLaunchFactory _launches;

    readonly Dictionary<Guid, TerminalTabs> _tabs = new();
    readonly State<TerminalTabs?> _active = new(null);

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
        IClipboard? clipboard = null,
        ITerminalPalette? palette = null)
    {
        _registry = registry;
        _ptys = ptys;
        _engines = engines;
        _dispatcher = dispatcher;
        _clipboard = clipboard;
        _palette = palette;
        _launches = launches ?? DefaultLaunch;

        // Only the shell launch spawns anything, and only a spawn has to say what colour the pane
        // is. A caller that brings its own launch brings whatever that launch needs with it, which
        // is why this is checked here rather than required of everyone.
        if (launches is null && palette is null)
            throw new ArgumentNullException(
                nameof(palette),
                "A store that starts real shells needs the palette they are told the pane's colours from.");
    }

    public IReadable<TerminalTabs?> Tabs => _active;

    public bool HasLiveShell(Guid repoId) =>
        _tabs.TryGetValue(repoId, out var tabs) && tabs.HasLiveShell;

    public IReadOnlyList<Guid> ReposWithLiveShells() =>
        _tabs.Where(pair => pair.Value.HasLiveShell).Select(pair => pair.Key).ToArray();

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;

        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());

        // Every change rather than the removals alone: a list says it was cleared or reset without
        // saying what left, so what is gone is read off the list itself. A repository that is no
        // longer open has no working tree for a shell to sit in, and reconciliation removes rows
        // the user never touched — a pruned worktree takes its terminals with it.
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
    }

    void OnActiveChanged()
    {
        if (_disposed) return;

        _active.Value = _registry.Active.Value is { } repo ? TabsFor(repo) : null;
    }

    TerminalTabs TabsFor(Repo repo)
    {
        if (_tabs.TryGetValue(repo.Id, out var existing)) return existing;

        var tabs = new TerminalTabs(() => new TerminalInstance(_launches(repo), _dispatcher));
        _tabs[repo.Id] = tabs;
        return tabs;
    }

    void DropClosedRepos()
    {
        if (_disposed || _tabs.Count == 0) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        var closed = _tabs.Keys.Where(id => !open.Contains(id)).ToArray();

        foreach (var id in closed)
        {
            var tabs = _tabs[id];
            _tabs.Remove(id);

            // The pane is showing them, and is about to be shown a repository that no longer exists
            // either. Cleared before disposal so nothing draws a screen whose session has gone.
            if (ReferenceEquals(_active.Value, tabs)) _active.Value = null;

            tabs.Dispose();
        }
    }

    // Reached only when the constructor accepted no launch of its own, which is the one case the
    // constructor insisted on a palette for.
    ITerminalLaunch DefaultLaunch(Repo repo) =>
        new ShellLaunch(repo.Path, _ptys, _engines, _palette!, _clipboard);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _activeSub?.Dispose();
        _reposSub?.Dispose();

        // The pane may still be mounted: the application disposes its services before it unmounts
        // its view tree. Nothing may be pointing at a terminal whose shell is being killed.
        _active.Value = null;

        foreach (var tabs in _tabs.Values) tabs.Dispose();
        _tabs.Clear();
        _active.Dispose();
    }
}

/// <summary>What a repository's terminal runs. Substituted in tests, which have no shell to spawn.</summary>
internal delegate ITerminalLaunch TerminalLaunchFactory(Repo repo);
