using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// The cheap, all-repos per-repo signals, aggregated from the single git-status probe (branch /
// ahead / behind / dirty) plus the operations store (busy / unseen-error). One type feeds the
// RepoBar row dot, the toolbar's push/pull availability + dirty-for-stash, the status bar, and the
// branches header. Add new cheap signals as fields here rather than threading another store.
public sealed record RepoStatus(
    string? CurrentBranchName,
    bool IsDetached,
    bool HasUpstream,
    int Ahead,
    int Behind,
    bool IsDirty,
    bool IsBusy,
    bool HasUnseenError)
{
    public static readonly RepoStatus Unknown = new(null, false, false, 0, 0, false, false, false);
}

// Single source of truth for the cheap per-repo signals, including the checked-out branch's
// ahead/behind — nothing else in the app may hold a second copy of those. One probe per repo, so
// every RepoBar row (not just the active one) can show its branch state and dirtiness.
public interface IRepoStatusStore
{
    // The active repo's status. Swaps on repo switch; recomputes when its probe or op state changes.
    IReadable<RepoStatus> Active { get; }

    // Any repo's status. Call inside a reactive binding (rows) — the underlying probe + op-state
    // reads are auto-tracked, so the row updates live.
    RepoStatus For(Guid repoId);
}

// The write side of IRepoStatusStore's per-repo slot, for a summary observed by another store's git
// read. Two phase because the ordering is decided when the read *starts*, not when it lands: Reserve
// takes the repo's next probe epoch exactly as an internal probe would, and Publish is dropped if a
// newer probe or reservation has happened since. Deliberately not a member of IRepoStatusStore — that
// interface is the read seam five view models hold, and none of them writes.
internal interface IRepoStatusIngest
{
    int Reserve(Guid repoId);
    void Publish(Guid repoId, int reservation, GitStatusSummary? summary);
}

/// <summary>
/// Owns one cheap <c>git status --porcelain=v2 --branch</c> probe per repo (branch / ahead / behind
/// / dirty) and composes it with the operations store's busy + unseen-error state into a single
/// <see cref="RepoStatus"/>. The probe is the heavy-data snapshot store's cheap counterpart: it
/// covers <em>all</em> repos (so nested rows get live decorations) while the snapshot store stays
/// bounded to the active + warm set for the expensive slices (commit graph, full file lists,
/// branch listing).
///
/// Probes refresh proactively on <see cref="Start"/>, on repo add, and whenever the active repo
/// changes, and per-repo on <see cref="WorkingTreeChangedMessage"/> / <see cref="RefsChangedMessage"/>
/// / <see cref="CommitCreatedMessage"/> (the watcher emits these for all repos, not just active).
/// Each refresh is generation-guarded so a slow result can't clobber a newer one, and every probe
/// runs under the shared <see cref="IGitReadGate"/> so a big repo tree doesn't burst at startup.
/// </summary>
internal sealed class RepoStatusStore : IRepoStatusStore, IRepoStatusIngest, IHostedService, IDisposable
{
    private readonly IRepoOperationsStore _ops;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly IStartupSweepCoordinator _sweep;
    private readonly IGitReadGate _gate;
    private readonly IUiDispatcher _dispatcher;
    private bool _started;
    private bool _disposed;

    // Per-repo probe result + a per-repo generation counter for ordered refreshes. UI-thread only.
    private readonly Dictionary<Guid, State<GitStatusSummary>> _probe = new();
    private readonly Dictionary<Guid, int> _epoch = new();

    private readonly Derived<RepoStatus> _active;

    private IDisposable? _reposSub;
    private IDisposable? _activeSub;
    private IDisposable? _workingTreeSub;
    private IDisposable? _refsSub;
    private IDisposable? _commitSub;
    private IDisposable? _optimisticSyncSub;
    private IDisposable? _refreshSub;

    public IReadable<RepoStatus> Active => _active;

    public RepoStatusStore(IRepoOperationsStore ops, IRepoRegistry registry, IGitService git, IMessageBus bus, IStartupSweepCoordinator sweep, IGitReadGate gate, IUiDispatcher dispatcher)
    {
        _ops = ops;
        _registry = registry;
        _git = git;
        _bus = bus;
        _sweep = sweep;
        _gate = gate;
        _dispatcher = dispatcher;
        // Recomputes whenever the active repo, its probe, or its op state changes — all tracked.
        _active = new Derived<RepoStatus>(() =>
        {
            var repo = _registry.Active.Value;
            return repo == null ? RepoStatus.Unknown : For(repo.Id);
        });
    }

    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;
        // Working-tree + commit changes on the *active* repo are already carried by the file-list
        // reload's `git status`, which RepoSnapshotStore ingests — probing here would run the same
        // working-tree walk twice. Refs (a fetch moving ahead/behind without touching the tree) and
        // the user's explicit refresh keep their probe.
        _workingTreeSub = _bus.SubscribeScoped<WorkingTreeChangedMessage>(m => RefreshUnlessActive(m.RepoId));
        _refsSub = _bus.SubscribeScoped<RefsChangedMessage>(m => Refresh(m.RepoId));
        _commitSub = _bus.SubscribeScoped<CommitCreatedMessage>(m => RefreshUnlessActive(m.RepoId));
        _refreshSub = _bus.SubscribeScoped<RepoRefreshRequestedMessage>(m => Refresh(m.RepoId));
        _optimisticSyncSub = _bus.SubscribeScoped<RemoteSyncOptimisticMessage>(ApplyOptimisticSync);
        // Subscribe fires Reset immediately with the current list, seeding a probe for every repo.
        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
        // A switch has to re-probe: every consumer reads the *active* repo's slot, so without this
        // the toolbar, the status bar and the branches badge all keep showing the previous repo's
        // numbers until an unrelated message happens to fire. Subscribing fires immediately, which
        // also seeds the active repo ahead of the startup sweep the Reset above defers.
        _activeSub = _registry.Active.Subscribe(repo =>
        {
            if (repo != null) Refresh(repo.Id);
        });
    }

    public RepoStatus For(Guid repoId)
    {
        var p = Probe(repoId).Value;
        return new RepoStatus(
            p.Branch, p.IsDetached, p.HasUpstream, p.Ahead, p.Behind, p.IsDirty,
            IsBusy: _ops.IsBusy(repoId),
            HasUnseenError: _ops.HasUnseenError(repoId));
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                // Defer the startup probe burst until the active repo's first load has landed.
                _sweep.RunInitialSweep(() =>
                {
                    foreach (var r in _registry.Repos) Refresh(r.Id);
                });
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added) Refresh(added.Id);
                break;
            case ListChangeKind.Replaced:
                if (change.Item is { } replaced) Refresh(replaced.Id);
                break;
            // Removed / Moved / Cleared: nothing to (re)probe.
        }
    }

    // Returns the per-repo probe state, creating it (default Unknown) on first access so a row's
    // binding has a stable observable to subscribe to even before the first probe posts back.
    private State<GitStatusSummary> Probe(Guid id)
    {
        if (!_probe.TryGetValue(id, out var s))
        {
            s = new State<GitStatusSummary>(GitStatusSummary.Unknown);
            _probe[id] = s;
        }
        return s;
    }

    // Patches a repo's ahead/behind to the known post-sync outcome immediately, ahead of the
    // reconciling probe (which the accompanying RefsChangedMessage already kicked, bumping the
    // epoch so any older in-flight probe is dropped). Components left null stay as the probe found
    // them. UI-thread only, like every other probe-state write here.
    private void ApplyOptimisticSync(RemoteSyncOptimisticMessage msg)
    {
        if (_disposed) return;
        var state = Probe(msg.RepoId);
        var cur = state.Value;
        var next = cur;
        if (msg.Ahead is { } ahead) next = next with { Ahead = ahead };
        if (msg.Behind is { } behind) next = next with { Behind = behind };
        if (next != cur) state.Value = next;
    }

    // The active repo's file-list reload already runs a `git status` that carries the summary, and
    // RepoSnapshotStore ingests it; a probe here would be that working-tree walk a second time.
    private void RefreshUnlessActive(Guid repoId)
    {
        if (_registry.Active.Value?.Id == repoId) return;
        Refresh(repoId);
    }

    // Takes the repo's next probe epoch, mutating the same counter Refresh uses, so an ingested
    // observation orders against concurrent probes the way two probes order against each other.
    // UI-thread only, called by RepoSnapshotStore before its local read starts.
    public int Reserve(Guid repoId)
    {
        var gen = (_epoch.TryGetValue(repoId, out var e) ? e : 0) + 1;
        _epoch[repoId] = gen;
        return gen;
    }

    // Writes a summary observed by RepoSnapshotStore's file-list read into this repo's slot, ordered
    // by the reservation it took when that read started. Dropped if a newer probe or reservation has
    // since claimed the slot. A null summary means the read failed: fall back to the probe this read
    // replaced, or root cause D's failed-load-keeps-stale-summary gets no recovery path on this
    // channel now that the probe was skipped. UI-thread only, like every write to _probe.
    public void Publish(Guid repoId, int reservation, GitStatusSummary? summary)
    {
        if (_disposed) return;
        if (!_epoch.TryGetValue(repoId, out var cur) || cur != reservation) return;
        if (summary == null) { Refresh(repoId); return; }
        Probe(repoId).Value = summary;
    }

    private void Refresh(Guid repoId)
    {
        var dispatcher = _dispatcher;
        var repo = FindRepo(repoId);
        if (repo == null) return;
        var state = Probe(repoId);
        var gen = Reserve(repoId);
        _ = Task.Run(async () =>
        {
            GitStatusSummary? summary;
            using (await _gate.Acquire(repoId, GitReadKind.Status))
            {
                try { summary = _git.GetStatusSummary(repo); }
                catch { summary = null; }
            }
            dispatcher.Post(() =>
            {
                if (_disposed) return;
                // Drop a result superseded by a newer refresh for the same repo.
                if (_epoch.TryGetValue(repoId, out var cur) && cur != gen) return;
                // A failed probe (null) keeps the last known status: zeroing ahead/upstream here
                // would silently disable push/pull in the toolbar while the branches view still
                // shows the cached counts. The actual operations report their own errors.
                if (summary != null) state.Value = summary;
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
        if (_disposed) return;
        _disposed = true;
        _reposSub?.Dispose();
        _activeSub?.Dispose();
        _workingTreeSub?.Dispose();
        _refsSub?.Dispose();
        _commitSub?.Dispose();
        _optimisticSyncSub?.Dispose();
        _refreshSub?.Dispose();
        _active.Dispose();
        foreach (var s in _probe.Values) s.Dispose();
        _probe.Clear();
    }
}
