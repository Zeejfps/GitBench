using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// The active repo's working-tree snapshot plus its submodule drift list, bundled so the two
// always move together. Mirrors LocalChangesViewModel's private LoadResult minus the amend-only
// staged-vs-parent list, which stays view-model-local.
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
    IReadable<Fetched<CommitSnapshot>?> Commits { get; }
    IReadable<Fetched<BranchListing>?> Branches { get; }
    IReadable<Fetched<LocalChangesData>?> LocalChanges { get; }
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
internal sealed class RepoSnapshotStore : IRepoSnapshotStore, IHostedService, IDisposable
{
    private const int MaxCommits = 3000;

    // If the active repo's first load never lands (no active repo, a load error, a hang), release
    // the deferred startup sweeps anyway after this long so per-repo decorations aren't blocked.
    private const int ActiveReadyFallbackMs = 5000;

    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly IStartupSweepCoordinator _sweep;
    // The active repo's first load kicks three slice loads; once all three have landed the active
    // repo is "ready" and the deferred all-repos sweeps may run. UI-thread only.
    private int _firstLoadRemaining = 3;
    private readonly IUiDispatcher _dispatcher;
    private bool _started;

    private readonly State<Fetched<CommitSnapshot>?> _commits = new(null);
    private readonly State<Fetched<BranchListing>?> _branches = new(null);
    private readonly State<Fetched<LocalChangesData>?> _local = new(null);

    private readonly RepoSnapshotCache<Fetched<CommitSnapshot>> _commitsCache = new();
    private readonly RepoSnapshotCache<Fetched<BranchListing>> _branchesCache = new();
    private readonly RepoSnapshotCache<Fetched<LocalChangesData>> _localCache = new();

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
    private IDisposable? _remoteSyncSub;
    private IDisposable? _refreshSub;

    public IReadable<Fetched<CommitSnapshot>?> Commits => _commits;
    public IReadable<Fetched<BranchListing>?> Branches => _branches;
    public IReadable<Fetched<LocalChangesData>?> LocalChanges => _local;

    public RepoSnapshotStore(
        IRepoRegistry registry,
        IGitService git,
        IMessageBus bus,
        IStartupSweepCoordinator sweep,
        IUiDispatcher dispatcher)
    {
        _registry = registry;
        _git = git;
        _bus = bus;
        _sweep = sweep;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Wires up loading once the host starts hosted services (after Build). Until this runs the
    /// store is inert and its slices read null — view models simply show "Loading…". Subscribing to
    /// <see cref="IRepoRegistry.Active"/> fires immediately, seeding the first load.
    /// </summary>
    public void Start()
    {
        if (_started) return; // idempotent
        _started = true;
        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _refsSub = _bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged);
        _workingTreeSub = _bus.SubscribeScoped<WorkingTreeChangedMessage>(OnWorkingTreeChanged);
        _commitCreatedSub = _bus.SubscribeScoped<CommitCreatedMessage>(OnCommitCreated);
        _submodulesSub = _bus.SubscribeScoped<SubmodulesChangedMessage>(OnSubmodulesChanged);
        _remoteSyncSub = _bus.SubscribeScoped<RemoteSyncOptimisticMessage>(OnRemoteSyncOptimistic);
        _refreshSub = _bus.SubscribeScoped<RepoRefreshRequestedMessage>(OnRefreshRequested);

        // Safety net for the active-ready signal below: if the first load never lands, release the
        // deferred startup sweeps anyway so per-repo decorations and discovery still run.
        Task.Run(async () =>
        {
            await Task.Delay(ActiveReadyFallbackMs).ConfigureAwait(false);
            _dispatcher.Post(_sweep.MarkActiveReady);
        });
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
            // No active repo to wait on — let the deferred all-repos sweeps run.
            _sweep.MarkActiveReady();
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

    // Snap the current branch's ahead/behind badge to the known post-sync outcome (push → ahead 0,
    // pull → behind 0) so the Branches list matches the RepoBar/toolbar instantly instead of trailing
    // until the heavier branch reload (kicked by the accompanying RefsChangedMessage) lands. Patches
    // the cache too so a switch-away-and-back doesn't briefly show the pre-sync numbers. Best-effort:
    // only the already-tracked HEAD branch is touched; everything else waits for the reload.
    private void OnRemoteSyncOptimistic(RemoteSyncOptimisticMessage msg)
    {
        var active = _registry.Active.Value;
        if (active != null && active.Id == msg.RepoId)
        {
            if (_branches.Value is { } current)
            {
                var patched = PatchHeadSync(current, msg.Ahead, msg.Behind);
                if (!ReferenceEquals(patched, current))
                {
                    _branches.Value = patched;
                    _branchesCache.Set(msg.RepoId, patched);
                }
            }
            return;
        }

        if (_branchesCache.TryGet(msg.RepoId, out var cached) && cached != null)
        {
            var patched = PatchHeadSync(cached, msg.Ahead, msg.Behind);
            if (!ReferenceEquals(patched, cached))
                _branchesCache.Set(msg.RepoId, patched);
        }
    }

    // Returns the listing with the HEAD branch's ahead/behind set to the given components (a null
    // component is left as loaded). Returns the same instance when there's nothing to change — not
    // Ok, no HEAD branch, an untracked/gone HEAD (no badge to snap), or already at the target — so
    // callers can skip the write and the equality-deduped State stays quiet.
    private static Fetched<BranchListing> PatchHeadSync(Fetched<BranchListing> fetched, int? ahead, int? behind)
    {
        if (fetched is not Fetched<BranchListing>.Ok ok) return fetched;
        var locals = ok.Value.LocalBranches;
        var headIdx = -1;
        for (var i = 0; i < locals.Count; i++)
            if (locals[i].IsHead) { headIdx = i; break; }
        if (headIdx < 0) return fetched;

        var head = locals[headIdx];
        if (head.UpstreamState != BranchUpstreamState.Tracked) return fetched;

        var newAhead = ahead ?? head.AheadBy;
        var newBehind = behind ?? head.BehindBy;
        if (newAhead == head.AheadBy && newBehind == head.BehindBy) return fetched;

        var newLocals = new List<BranchEntry>(locals);
        newLocals[headIdx] = head with { AheadBy = newAhead, BehindBy = newBehind };
        return new Fetched<BranchListing>.Ok(ok.Value with { LocalBranches = newLocals });
    }

    // Explicit user retry after a failed load. The local slice is nulled before reloading so the
    // view model re-renders even when the retry fails with a byte-identical error (the equality-
    // deduped State would otherwise swallow it and the placeholder would sit on stale "Loading…").
    private void OnRefreshRequested(RepoRefreshRequestedMessage msg)
    {
        var active = _registry.Active.Value;
        if (active == null || active.Id != msg.RepoId) return;
        _local.Value = null;
        ReloadCommits(active);
        ReloadBranches(active);
        ReloadLocal(active);
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

    // Unlike GetBranches/GetLocalChanges, the commit Load can throw. Fold the throw into
    // Failed so the failure reaches the view model (which renders it) instead of being
    // swallowed into a perpetual "Loading…".
    private Fetched<CommitSnapshot> LoadCommits(Repo repo)
    {
        try { return _git.Load(repo, MaxCommits); }
        catch (Exception ex)
        {
            return new Fetched<CommitSnapshot>.Failed(ex.Message);
        }
    }

    private void ReloadBranches(Repo repo) =>
        LoadSlice(repo, _branchesLane, _branchesCache, _branches, r => _git.GetBranches(r));

    private void ReloadLocal(Repo repo) =>
        LoadSlice(repo, _localLane, _localCache, _local, LoadLocalChanges);

    private Fetched<LocalChangesData> LoadLocalChanges(Repo repo)
        => _git.GetLocalChanges(repo).Map(snap => BuildLocalData(repo, snap));

    private LocalChangesData BuildLocalData(Repo repo, LocalChangesSnapshot snap)
    {
        IReadOnlyList<SubmoduleInfo> drift = Array.Empty<SubmoduleInfo>();
        // Submodules are one level deep in our model, so a submodule row has no nested drift.
        if (!repo.IsSubmodule)
        {
            var subs = _git.ListSubmodules(repo);
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
        WarmSlice(repo, _commitsCache, LoadCommits, static s => s is Fetched<CommitSnapshot>.Failed);

    private void WarmBranches(Repo repo) =>
        WarmSlice(repo, _branchesCache, r => _git.GetBranches(r), static b => b is Fetched<BranchListing>.Failed);

    private void WarmLocal(Repo repo) =>
        WarmSlice(repo, _localCache, LoadLocalChanges, static d => d is Fetched<LocalChangesData>.Failed);

    // Background-refresh a warm (non-active) repo's cached slice so a later switch-back is instant
    // and current. Unlike LoadSlice it never touches the exposed active State — it only updates the
    // cache, and it skips error results so a transient failure can't poison the cache. Best-effort:
    // no generation guard (a switch-back always reloads anyway), so concurrent warms are last-write.
    private void WarmSlice<T>(Repo repo, RepoSnapshotCache<T> cache, Func<Repo, T> work, Func<T, bool> hasError)
        where T : class
    {
        var dispatcher = _dispatcher;
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
        var gen = lane.Bump();
        Task.Run(() =>
        {
            T? result = null;
            try { result = work(repo); }
            catch { result = null; }

            dispatcher.Post(() =>
            {
                // First three slice loads landing = active repo ready; release the startup sweeps.
                if (_firstLoadRemaining > 0 && --_firstLoadRemaining == 0)
                    _sweep.MarkActiveReady();
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
        _remoteSyncSub?.Dispose();
        _refreshSub?.Dispose();
        _commits.Dispose();
        _branches.Dispose();
        _local.Dispose();
    }
}
