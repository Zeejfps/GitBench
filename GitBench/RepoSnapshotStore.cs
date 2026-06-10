using ZGF.Observable;

namespace GitBench;

// The active repo's working-tree snapshot plus its submodule drift list, bundled so the two
// always move together. Mirrors LocalChangesViewModel's private LoadResult minus the amend-only
// HeadFiles, which stays view-model-local.
public sealed record LocalChangesData(
    LocalChangesSnapshot Snapshot,
    IReadOnlyList<SubmoduleInfo> Drift,
    // Default merge message when a merge is in progress, else null. Lets the commit box
    // double as the "finish merge" UI.
    string? MergeMessage = null);

// Single source of truth for the active repo's *heavy* loaded git data (commit graph, branch
// listing, full file lists). View models project from these observables instead of each running
// their own load + cache. The cheap per-repo signals (branch / ahead / behind / dirty) live in
// IRepoStatusStore instead, which covers all repos rather than just the active + warm set.
internal interface IRepoSnapshotStore
{
    IReadable<CommitSnapshot?> Commits { get; }
    IReadable<BranchListing?> Branches { get; }
    IReadable<LocalChangesData?> LocalChanges { get; }
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

    private readonly RepoSnapshotCache<CommitSnapshot> _commitsCache = new();
    private readonly RepoSnapshotCache<BranchListing> _branchesCache = new();
    private readonly RepoSnapshotCache<LocalChangesData> _localCache = new();

    private readonly GenerationGuard _commitsLane = new();
    private readonly GenerationGuard _branchesLane = new();
    private readonly GenerationGuard _localLane = new();

    // The N most-recently-active repos form the "warm set": their caches are refreshed in the
    // background when their files change, so switching among them is instant *and* current.
    // Most-recently-active first; index 0 is the active repo (refreshed via the active path).
    private const int WarmRepoCount = 4;
    private readonly List<Guid> _recent = new();

    private IDisposable? _activeSub;
    private IDisposable? _refsSub;
    private IDisposable? _workingTreeSub;
    private IDisposable? _commitCreatedSub;
    private IDisposable? _submodulesSub;

    public IReadable<CommitSnapshot?> Commits => _commits;
    public IReadable<BranchListing?> Branches => _branches;
    public IReadable<LocalChangesData?> LocalChanges => _local;

    public RepoSnapshotStore(
        IRepoRegistry registry,
        IGitService git,
        IMessageBus bus)
    {
        _registry = registry;
        _git = git;
        _bus = bus;
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

        TouchRecent(repo.Id);

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
        var active = _registry.Active.Value;
        if (active != null && active.Id == msg.RepoId)
        {
            ReloadCommits(active);
            ReloadBranches(active);
        }
        else if (WarmRepo(msg.RepoId) is { } warm)
        {
            WarmCommits(warm);
            WarmBranches(warm);
        }
    }

    private void OnCommitCreated(CommitCreatedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == msg.RepoId)
        {
            ReloadCommits(active);
            ReloadBranches(active);
            ReloadLocal(active);
        }
        else if (WarmRepo(msg.RepoId) is { } warm)
        {
            WarmCommits(warm);
            WarmBranches(warm);
            WarmLocal(warm);
        }
    }

    private void OnWorkingTreeChanged(WorkingTreeChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == msg.RepoId)
            ReloadLocal(active);
        else if (WarmRepo(msg.RepoId) is { } warm)
            WarmLocal(warm);
    }

    private void OnSubmodulesChanged(SubmodulesChangedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active != null && PrimaryId(active) == msg.PrimaryRepoId)
        {
            ReloadLocal(active);
            return;
        }
        // Warm any non-active warm repo whose primary matches the changed submodule set.
        foreach (var id in _recent.Take(WarmRepoCount))
        {
            if (active != null && id == active.Id) continue;
            var r = FindRepo(id);
            if (r != null && PrimaryId(r) == msg.PrimaryRepoId)
                WarmLocal(r);
        }
    }

    // ---- warm set ----

    private void TouchRecent(Guid id)
    {
        _recent.Remove(id);
        _recent.Insert(0, id);
        // Bound the list; the warm set is only the first WarmRepoCount, but keep a little history
        // so a repo that briefly drops out doesn't lose its place immediately.
        const int maxTracked = 32;
        if (_recent.Count > maxTracked) _recent.RemoveRange(maxTracked, _recent.Count - maxTracked);
    }

    // Returns the repo for msg.RepoId iff it's a non-active member of the warm set; else null.
    private Repo? WarmRepo(Guid id)
    {
        if (_registry.Active.Value?.Id == id) return null;
        var idx = _recent.IndexOf(id);
        if (idx < 0 || idx >= WarmRepoCount) return null;
        return FindRepo(id);
    }

    private Repo? FindRepo(Guid id)
    {
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r;
        return null;
    }

    private static Guid PrimaryId(Repo repo) => repo.IsPrimary ? repo.Id : (repo.ParentRepoId ?? repo.Id);

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
        return new LocalChangesData(snap, drift, _git.GetMergeMessage(repo));
    }

    // ---- warm loads (non-active repos: refresh the cache only, never the exposed state) ----

    private void WarmCommits(Repo repo) =>
        WarmSlice(repo, _commitsCache, LoadCommits, s => s.ErrorMessage != null);

    private void WarmBranches(Repo repo) =>
        WarmSlice(repo, _branchesCache, r => _git.GetBranches(r), b => b.ErrorMessage != null);

    private void WarmLocal(Repo repo) =>
        WarmSlice(repo, _localCache, LoadLocalChanges, d => d.Snapshot.ErrorMessage != null);

    // Background-refresh a warm (non-active) repo's cached slice so a later switch-back is instant
    // and current. Unlike LoadSlice it never touches the exposed active State — it only updates the
    // cache, and it skips error results so a transient failure can't poison the cache. Best-effort:
    // no generation guard (a switch-back always reloads anyway), so concurrent warms are last-write.
    private void WarmSlice<T>(Repo repo, RepoSnapshotCache<T> cache, Func<Repo, T> work, Func<T, bool> hasError)
        where T : class
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        Task.Run(() =>
        {
            T? result = null;
            try { result = work(repo); }
            catch { result = null; }
            if (result == null) return;
            dispatcher.Post(() =>
            {
                if (!hasError(result)) cache.Set(repo.Id, result);
            });
        });
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

    public void Dispose()
    {
        _activeSub?.Dispose();
        _refsSub?.Dispose();
        _workingTreeSub?.Dispose();
        _commitCreatedSub?.Dispose();
        _submodulesSub?.Dispose();
        _commits.Dispose();
        _branches.Dispose();
        _local.Dispose();
    }
}
