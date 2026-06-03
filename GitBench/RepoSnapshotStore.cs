using ZGF.Observable;

namespace GitGui;

// The active repo's working-tree snapshot plus its submodule drift list, bundled so the two
// always move together. Mirrors LocalChangesViewModel's private LoadResult minus the amend-only
// HeadFiles, which stays view-model-local.
public sealed record LocalChangesData(
    LocalChangesSnapshot Snapshot,
    IReadOnlyList<SubmoduleInfo> Drift);

// Single source of truth for the active repo's loaded git data. View models project from these
// observables instead of each running their own load + cache. PushStatus is *derived* from the
// branch listing (the HEAD entry), not loaded separately.
internal interface IRepoSnapshotStore
{
    IReadable<CommitSnapshot?> Commits { get; }
    IReadable<BranchListing?> Branches { get; }
    IReadable<LocalChangesData?> LocalChanges { get; }
    IReadable<PushStatus> PushStatus { get; }
}

/// <summary>
/// Owns per-repo loading and caching of the core git slices (commits, branches, local changes)
/// and exposes the *active* repo's slices as observables. Modeled on the other long-lived
/// services (e.g. <see cref="WorktreeSyncService"/>): subscribes to <see cref="IRepoRegistry.Active"/>
/// and the data-changed bus messages, runs git work off the UI thread, and posts results back.
///
/// On a repo switch each slice is set to its cached value immediately (instant paint — the
/// soft-refresh that used to live in each view model) while a fresh load runs in the background.
/// Late loads warm the cache keyed by repo id but only touch the exposed active state when their
/// repo is still active, guarded per slice by a <see cref="GenerationGuard"/>.
/// </summary>
internal sealed class RepoSnapshotStore : IRepoSnapshotStore, IDisposable
{
    private const int MaxCommits = 3000;

    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    // Set in Start once the UI dispatcher exists (it's created inside GuiApp). Null until then.
    private IUiDispatcher? _dispatcher;

    private readonly State<CommitSnapshot?> _commits = new(null);
    private readonly State<BranchListing?> _branches = new(null);
    private readonly State<LocalChangesData?> _local = new(null);
    private readonly Derived<PushStatus> _pushStatus;

    private readonly RepoSnapshotCache<CommitSnapshot> _commitsCache = new();
    private readonly RepoSnapshotCache<BranchListing> _branchesCache = new();
    private readonly RepoSnapshotCache<LocalChangesData> _localCache = new();

    private readonly GenerationGuard _commitsLane = new();
    private readonly GenerationGuard _branchesLane = new();
    private readonly GenerationGuard _localLane = new();

    private IDisposable? _activeSub;
    private IDisposable? _refsSub;
    private IDisposable? _workingTreeSub;
    private IDisposable? _commitCreatedSub;
    private IDisposable? _submodulesSub;

    public IReadable<CommitSnapshot?> Commits => _commits;
    public IReadable<BranchListing?> Branches => _branches;
    public IReadable<LocalChangesData?> LocalChanges => _local;
    public IReadable<PushStatus> PushStatus => _pushStatus;

    public RepoSnapshotStore(
        IRepoRegistry registry,
        IGitService git,
        IMessageBus bus)
    {
        _registry = registry;
        _git = git;
        _bus = bus;

        // PushStatus recomputes whenever the branch listing or the active repo changes — the two
        // states it reads are tracked automatically by Derived. Safe to build before Start: it's
        // a pure projection of state that needs no dispatcher.
        _pushStatus = new Derived<PushStatus>(() => DerivePushStatus(_branches.Value, _registry.Active.Value));
    }

    /// <summary>
    /// Wires up loading once the UI dispatcher is available (it's created inside GuiApp, after
    /// this store is constructed so that view models can resolve it during startup). Until this
    /// runs the store is inert and its slices read null — view models simply show "Loading…".
    /// Subscribing to <see cref="IRepoRegistry.Active"/> fires immediately, seeding the first load.
    /// </summary>
    public void Start(IUiDispatcher dispatcher)
    {
        if (_dispatcher != null) return; // idempotent
        _dispatcher = dispatcher;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _refsSub = _bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged);
        _workingTreeSub = _bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged);
        _commitCreatedSub = _bus.SubscribeScoped<CommitCreatedMessage>(OnCommitCreated);
        _submodulesSub = _bus.SubscribeScoped<SubmodulesChangedMessage>(OnSubmodulesChanged);
    }

    // ---- triggers ----

    private void OnActiveChanged()
    {
        var repo = _registry.Active.Value;
        if (repo == null)
        {
            // Invalidate in-flight loads from the previous repo and clear the exposed slices.
            _commitsLane.Bump();
            _branchesLane.Bump();
            _localLane.Bump();
            _commits.Value = null;
            _branches.Value = null;
            _local.Value = null;
            return;
        }

        // Soft refresh: show cached data instantly (or null → "Loading…" downstream), then reload.
        _commits.Value = _commitsCache.TryGet(repo.Id, out var c) ? c : null;
        _branches.Value = _branchesCache.TryGet(repo.Id, out var b) ? b : null;
        _local.Value = _localCache.TryGet(repo.Id, out var l) ? l : null;

        ReloadCommits(repo);
        ReloadBranches(repo);
        ReloadLocal(repo);
    }

    private void OnRefsChanged(RefsChangedMessage msg)
    {
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        ReloadCommits(repo);
        ReloadBranches(repo);
    }

    private void OnCommitCreated(CommitCreatedMessage msg)
    {
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        ReloadCommits(repo);
        ReloadBranches(repo);
        ReloadLocal(repo);
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage msg)
    {
        var repo = _registry.Active.Value;
        if (repo == null || repo.Id != msg.RepoId) return;
        ReloadLocal(repo);
    }

    private void OnSubmodulesChanged(SubmodulesChangedMessage msg)
    {
        var repo = _registry.Active.Value;
        if (repo == null) return;
        var primaryId = repo.IsPrimary ? repo.Id : (repo.ParentRepoId ?? repo.Id);
        if (primaryId != msg.PrimaryRepoId) return;
        ReloadLocal(repo);
    }

    // ---- loads ----

    private void ReloadCommits(Repo repo) =>
        LoadSlice(repo, _commitsLane, _commitsCache, _commits, LoadCommits);

    // Unlike GetBranches/GetLocalChanges (which return a snapshot carrying ErrorMessage), the
    // commit Load can throw. Wrap it into an error snapshot so the failure reaches the view model
    // (which renders it) instead of being swallowed into a perpetual "Loading…".
    private CommitSnapshot LoadCommits(Repo repo)
    {
        try { return _git.Load(repo, MaxCommits); }
        catch (Exception ex)
        {
            return new CommitSnapshot(repo.Id, repo.Path, Array.Empty<CommitNode>(), 0, false, ex.Message);
        }
    }

    private void ReloadBranches(Repo repo) =>
        LoadSlice(repo, _branchesLane, _branchesCache, _branches, r => _git.GetBranches(r));

    private void ReloadLocal(Repo repo) =>
        LoadSlice(repo, _localLane, _localCache, _local, LoadLocalChanges);

    private LocalChangesData LoadLocalChanges(Repo repo)
    {
        var snap = _git.GetLocalChanges(repo);
        IReadOnlyList<SubmoduleInfo> drift = Array.Empty<SubmoduleInfo>();
        // Submodules are one level deep in our model, so a submodule row has no nested drift.
        if (!repo.IsSubmodule)
        {
            var subs = _git.ListSubmodules(repo, out _);
            if (subs.Count > 0)
            {
                var driftList = new List<SubmoduleInfo>();
                foreach (var s in subs)
                {
                    // Drift = anything the parent should act on: skip in-sync and plain "modified"
                    // (the latter shows in the file list, not as a drift entry). Matches the prior
                    // LocalChangesViewModel filter.
                    if (s.Status == SubmoduleStatus.UpToDate) continue;
                    if (s.Status == SubmoduleStatus.Modified) continue;
                    driftList.Add(s);
                }
                drift = driftList;
            }
        }
        return new LocalChangesData(snap, drift);
    }

    // Runs the git read off-thread and posts back. Caches every successful result (keyed by repo,
    // so even a load superseded on this lane still warms switch-back), but only updates the exposed
    // active state when this load is the latest on its lane AND its repo is still active.
    private void LoadSlice<T>(
        Repo repo,
        GenerationGuard lane,
        RepoSnapshotCache<T> cache,
        State<T?> active,
        Func<Repo, T> work)
        where T : class
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return; // not started yet
        var gen = lane.Bump();
        Task.Run(() =>
        {
            T? result = null;
            try { result = work(repo); }
            catch { result = null; }

            dispatcher.Post(() =>
            {
                if (result != null) cache.Set(repo.Id, result);
                if (lane.IsStale(gen)) return;
                if (result != null && _registry.Active.Value?.Id == repo.Id)
                    active.Value = result;
            });
        });
    }

    // ---- push status derivation ----

    // PushStatus is the current branch's slice of the branch listing. With no listing yet (or an
    // unborn HEAD that has no branch entry) we fall back to Repo.Branch for the name so the header
    // doesn't blank during the first load / on a fresh repo.
    private static PushStatus DerivePushStatus(BranchListing? listing, Repo? active)
    {
        if (listing == null)
            return new PushStatus(active?.Branch, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false);

        BranchEntry? head = null;
        foreach (var b in listing.LocalBranches)
        {
            if (b.IsHead) { head = b; break; }
        }

        if (head == null)
        {
            // No HEAD branch: a populated listing means detached HEAD; an empty one means an
            // unborn HEAD (fresh repo) — keep the name fallback and don't claim detached.
            if (listing.LocalBranches.Count == 0)
                return new PushStatus(active?.Branch, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: false);
            return new PushStatus(null, HasUpstream: false, Ahead: 0, Behind: 0, IsDetached: true);
        }

        // Tracked == upstream set and remote ref exists; Gone/NeverLinked both mean "no upstream to
        // push/pull against", matching the old GetPushStatus (which keyed off whether @{u} resolved).
        var hasUpstream = head.UpstreamState == BranchUpstreamState.Tracked;
        return new PushStatus(
            head.Name,
            hasUpstream,
            head.AheadBy ?? 0,
            head.BehindBy ?? 0,
            IsDetached: false);
    }

    public void Dispose()
    {
        _activeSub?.Dispose();
        _refsSub?.Dispose();
        _workingTreeSub?.Dispose();
        _commitCreatedSub?.Dispose();
        _submodulesSub?.Dispose();
        _pushStatus.Dispose();
        _commits.Dispose();
        _branches.Dispose();
        _local.Dispose();
    }
}
