using ZGF.Observable;

namespace GitBench;

// Per-repo decorations for a RepoBar row, aggregated from the separate stores that own each signal:
// HasError from the operations store, IsDirty from this store's own working-tree tracking. Future
// signals (ahead/behind, conflicts, …) slot in here so a row picks them up without a new plumbing
// path.
public readonly record struct RepoBadges(bool HasError, bool IsDirty);

// Row-facing facade for the RepoBar badge dot. Rows depend on this single thing rather than on each
// underlying store, and read Badges() inside a reactive binding (the per-repo state reads are
// auto-tracked, so the dot updates live).
public interface IRepoBadgeStore
{
    RepoBadges Badges(Guid repoId);
}

/// <summary>
/// Tracks per-repo working-tree dirtiness for every repo shown in the RepoBar (not just the active
/// one) and combines it with the operations store's error state into a single <see cref="RepoBadges"/>.
///
/// Dirtiness is computed proactively: on <see cref="Start"/> we kick a background <c>git status</c>
/// for every repo in the registry, and refresh a repo whenever it's added or when its working tree /
/// commits change (the file watcher's per-repo <see cref="WorkingTreeChangedMessage"/> runs for all
/// repos, not just the active one). It is NOT lazy-on-render — rows build before the dispatcher is
/// wired, so a render-time load would silently no-op and leave non-active repos stuck reading false.
/// </summary>
internal sealed class RepoBadgeStore : IRepoBadgeStore, IDisposable
{
    private readonly IRepoOperationsStore _ops;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private IUiDispatcher? _dispatcher;
    private bool _disposed;

    // Per-repo dirty flag. UI-thread only.
    private readonly Dictionary<Guid, State<bool>> _dirty = new();

    private IDisposable? _workingTreeSub;
    private IDisposable? _commitSub;
    private IDisposable? _reposSub;

    public RepoBadgeStore(IRepoOperationsStore ops, IRepoRegistry registry, IGitService git, IMessageBus bus)
    {
        _ops = ops;
        _registry = registry;
        _git = git;
        _bus = bus;
    }

    public void Start(IUiDispatcher dispatcher)
    {
        if (_dispatcher != null) return; // idempotent
        _dispatcher = dispatcher;
        _workingTreeSub = _bus.SubscribeScoped<WorkingTreeChangedMessage>(m => Refresh(m.RepoId));
        _commitSub = _bus.SubscribeScoped<CommitCreatedMessage>(m => Refresh(m.RepoId));
        // Subscribe fires Reset immediately with the current list, seeding dirty for every repo.
        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
    }

    public RepoBadges Badges(Guid repoId) => new(
        HasError: _ops.HasUnseenError(repoId),
        IsDirty: Get(repoId).Value);

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                foreach (var r in _registry.Repos) Refresh(r.Id);
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added) Refresh(added.Id);
                break;
            case ListChangeKind.Replaced:
                if (change.Item is { } replaced) Refresh(replaced.Id);
                break;
            // Removed / Moved / Cleared: nothing to (re)compute.
        }
    }

    // Returns the per-repo flag, creating it (default false) on first access so a row's binding has a
    // stable observable to subscribe to even before the first git status posts back.
    private State<bool> Get(Guid id)
    {
        if (!_dirty.TryGetValue(id, out var s))
        {
            s = new State<bool>(false);
            _dirty[id] = s;
        }
        return s;
    }

    private void Refresh(Guid repoId)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        var repo = FindRepo(repoId);
        if (repo == null) return;
        var state = Get(repoId);
        Task.Run(() =>
        {
            bool dirty = false;
            try
            {
                var snap = _git.GetLocalChanges(repo);
                dirty = snap.Staged.Count + snap.Unstaged.Count > 0;
            }
            catch { dirty = false; }
            dispatcher.Post(() =>
            {
                if (!_disposed) state.Value = dirty;
            });
        });
    }

    private Repo? FindRepo(Guid id)
    {
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        _workingTreeSub?.Dispose();
        _commitSub?.Dispose();
        _reposSub?.Dispose();
        foreach (var s in _dirty.Values) s.Dispose();
        _dirty.Clear();
    }
}
