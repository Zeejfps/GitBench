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
    bool HasUnseenError,
    // The branch a checkout is switching to, held until a fresh read confirms HEAD landed. Non-null
    // means CurrentBranchName is still the branch the user just left.
    string? PendingBranchName)
{
    public static readonly RepoStatus Unknown = new(null, false, false, 0, 0, false, false, false, null);

    // True while HEAD is moving and no fresh read has confirmed where to. Every branch name on this
    // record is provisional until it clears — gate anything that would hand one to git as an argument.
    public bool IsHeadInMotion => PendingBranchName != null;

    // The branch HEAD is on, or is about to be on: the name to show the user, and the only one safe
    // to seed a follow-up operation with.
    public string? EffectiveBranchName => PendingBranchName ?? CurrentBranchName;
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
///
/// A refresh asks for the narrowest read that can answer it: refs moving (a fetch) takes the
/// refs-only <see cref="IGitStatusReader.GetSyncSummary"/>, and only a channel that can also have
/// changed the working tree pays for the full status walk. Refreshes are coalesced per repo — one
/// read in flight at a time, with at most one re-run queued behind it — so clicking fetch three
/// times costs one extra read rather than three, and doesn't discard the answer already on its way.
/// </summary>
internal sealed class RepoStatusStore : IRepoStatusStore, IRepoStatusIngest, IHostedService, IDisposable
{
    private readonly IRepoOperationsStore _ops;
    private readonly IRepoRegistry _registry;
    private readonly IGitStatusReader _git;
    private readonly IMessageBus _bus;
    private readonly IStartupSweepCoordinator _sweep;
    private readonly IGitReadGate _gate;
    private readonly IUiDispatcher _dispatcher;
    private readonly IRepoHeadStore _head;
    private readonly IRepoHeadConfirm _headConfirm;
    private bool _started;
    private bool _disposed;

    // Per-repo probe result + a per-repo generation counter for ordered refreshes. UI-thread only.
    private readonly Dictionary<Guid, State<GitStatusSummary>> _probe = new();
    private readonly Dictionary<Guid, int> _epoch = new();
    private readonly Dictionary<Guid, RefreshSlot> _refreshes = new();

    private readonly Derived<RepoStatus> _active;

    private IDisposable? _reposSub;
    private IDisposable? _activeSub;
    private IDisposable? _workingTreeSub;
    private IDisposable? _refsSub;
    private IDisposable? _commitSub;
    private IDisposable? _optimisticSyncSub;
    private IDisposable? _optimisticCommitSub;
    private IDisposable? _refreshSub;

    public IReadable<RepoStatus> Active => _active;

    public RepoStatusStore(IRepoOperationsStore ops, IRepoRegistry registry, IGitStatusReader git, IMessageBus bus, IStartupSweepCoordinator sweep, IGitReadGate gate, IUiDispatcher dispatcher, IRepoHeadStore head, IRepoHeadConfirm headConfirm)
    {
        _ops = ops;
        _registry = registry;
        _git = git;
        _bus = bus;
        _sweep = sweep;
        _gate = gate;
        _dispatcher = dispatcher;
        _head = head;
        _headConfirm = headConfirm;
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
        _refsSub = _bus.SubscribeScoped<RefsChangedMessage>(m => RefreshSync(m.RepoId));
        _commitSub = _bus.SubscribeScoped<CommitCreatedMessage>(m => RefreshUnlessActive(m.RepoId));
        _refreshSub = _bus.SubscribeScoped<RepoRefreshRequestedMessage>(m => Refresh(m.RepoId));
        _optimisticSyncSub = _bus.SubscribeScoped<RemoteSyncOptimisticMessage>(ApplyOptimisticSync);
        _optimisticCommitSub = _bus.SubscribeScoped<LocalCommitOptimisticMessage>(ApplyOptimisticCommit);
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
            HasUnseenError: _ops.HasUnseenError(repoId),
            // The probe names the branch the user left until a checkout settles; carrying the pending
            // name here is what stops five view models each answering "current branch" differently.
            PendingBranchName: _head.For(repoId).PendingBranch);
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

    // Grows the repo's ahead count by the one commit that just landed, ahead of the post-commit
    // reload that reconciles it. Skipped without a tracked upstream: Ahead means nothing then, and
    // the toolbar already enables push as a publish. UI-thread only, like every other probe write.
    private void ApplyOptimisticCommit(LocalCommitOptimisticMessage msg)
    {
        if (_disposed) return;
        var state = Probe(msg.RepoId);
        var cur = state.Value;
        if (cur.IsDetached || !cur.HasUpstream) return;
        state.Value = cur with { Ahead = cur.Ahead + 1 };
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
        _headConfirm.Confirm(repoId);
    }

    // The full probe: a working-tree walk, so it answers the dirty flag as well as the sync half.
    private void Refresh(Guid repoId) => Request(repoId, RefreshScope.Full);

    // Refs moved and the working tree did not (a fetch, a push): the dirty flag this repo already
    // carries still holds, so there is nothing to walk the tree for. The whole point of the split —
    // this is the read that decides how soon "22 behind" and the enabled pull button appear.
    private void RefreshSync(Guid repoId) => Request(repoId, RefreshScope.Sync);

    private void Request(Guid repoId, RefreshScope scope)
    {
        var repo = FindRepo(repoId);
        if (repo == null) return;
        var slot = Slot(repoId);
        if (slot.InFlight)
        {
            // A request arriving mid-read cannot be served by that read — it started before
            // whatever prompted this one. Queue exactly one re-run, at the wider of the two scopes:
            // starting a second read now would only bump the epoch and discard the answer already
            // on its way, which is what made clicking fetch repeatedly push the number further out.
            slot.Queued = slot.Queued == RefreshScope.Full ? RefreshScope.Full : scope;
            return;
        }
        slot.InFlight = true;
        Read(repo, scope);
    }

    private void Read(Repo repo, RefreshScope scope)
    {
        var repoId = repo.Id;
        var dispatcher = _dispatcher;
        var gen = Reserve(repoId);
        var full = scope == RefreshScope.Full;
        _ = Task.Run(async () =>
        {
            GitStatusSummary? summary = null;
            GitSyncSummary? sync = null;
            try
            {
                using (await _gate.Acquire(repoId, full ? GitReadKind.Status : GitReadKind.Sync))
                {
                    try
                    {
                        if (full) summary = _git.GetStatusSummary(repo);
                        else sync = _git.GetSyncSummary(repo);
                    }
                    catch { /* a failed read lands as null below */ }
                }
            }
            catch { /* the gate went away under shutdown; still land, to free the slot */ }
            dispatcher.Post(() => Land(repoId, gen, summary, sync));
        });
    }

    private void Land(Guid repoId, int gen, GitStatusSummary? summary, GitSyncSummary? sync)
    {
        if (_disposed) return;
        var slot = Slot(repoId);
        slot.InFlight = false;

        // Drop a result superseded by a newer refresh for the same repo. A failed read (both null)
        // keeps the last known status: zeroing ahead/upstream here would silently disable push/pull
        // in the toolbar while the branches view still shows the cached counts. The actual
        // operations report their own errors. It also settles nothing — a read that told us nothing
        // can't confirm where HEAD landed.
        if (!_epoch.TryGetValue(repoId, out var cur) || cur == gen)
        {
            if (summary != null) Probe(repoId).Value = summary;
            // A sync read never looked at the working tree, so it patches the sync half onto the
            // dirty flag the last full observation left. That flag is re-observed on every
            // working-tree change, which is the only thing that can have moved it.
            else if (sync != null) Probe(repoId).Value = Probe(repoId).Value.With(sync);

            if (summary != null || sync != null) _headConfirm.Confirm(repoId);
        }

        if (slot.Queued is { } next)
        {
            slot.Queued = null;
            Request(repoId, next);
        }
    }

    private RefreshSlot Slot(Guid id)
    {
        if (!_refreshes.TryGetValue(id, out var slot))
        {
            slot = new RefreshSlot();
            _refreshes[id] = slot;
        }
        return slot;
    }

    // How much of the status a refresh needs to read.
    private enum RefreshScope
    {
        Sync,
        Full,
    }

    // One repo's refresh slot: whether a read is in flight and the single re-run queued behind it.
    private sealed class RefreshSlot
    {
        public bool InFlight;
        public RefreshScope? Queued;
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
        _optimisticCommitSub?.Dispose();
        _refreshSub?.Dispose();
        _active.Dispose();
        foreach (var s in _probe.Values) s.Dispose();
        _probe.Clear();
        _refreshes.Clear();
    }
}
