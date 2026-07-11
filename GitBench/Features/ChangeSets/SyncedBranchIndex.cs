using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.ChangeSets;

/// <summary>
/// App singleton that detects cross-repo change sets by branch-name convention (Locked decision #1):
/// within each sidebar group, a local branch name carried by two or more primaries is an implicit
/// set. Each member's own default branch is excluded, so <c>main</c> everywhere is not a set;
/// detached HEADs carry no branch name and are excluded implicitly. Worktrees/submodules never
/// participate — only primaries appear in a group's RepoIds.
///
/// Data source: <see cref="IGitService.GetBranches"/> looped over a group's primaries on a deferred
/// startup sweep (the <see cref="WorktreeSyncService"/> loop-and-schedule precedent) and refreshed
/// per repo on <see cref="RefsChangedMessage"/> (the <see cref="RepoStatusStore"/> message-driven
/// precedent). Correlation is computed on demand per group from the small per-repo snapshots — there
/// is no user-facing multi-repo operation yet, so the read side is cheap. <see cref="Revision"/>
/// bumps whenever a snapshot changes, so a bound view (the sidebar's synced glyph) re-derives.
/// </summary>
internal sealed class SyncedBranchIndex : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IMessageBus _bus;
    private readonly IStartupSweepCoordinator _sweep;
    private IUiDispatcher? _dispatcher;
    private bool _disposed;

    // Per-primary snapshot (default branch + local branch names). UI-thread only, like the other
    // stores' probe state. A per-repo epoch drops a slow result superseded by a newer refresh.
    private readonly Dictionary<Guid, RepoBranchSnapshot> _snapshots = new();
    private readonly Dictionary<Guid, int> _epoch = new();

    // Bumped on any snapshot change so a reactive reader (BranchesViewModel's row projection)
    // recomputes when detection changes.
    private readonly State<int> _revision = new(0);
    public IReadable<int> Revision => _revision;

    private IDisposable? _reposSub;
    private IDisposable? _refsSub;

    public SyncedBranchIndex(IRepoRegistry registry, IGitService git, IMessageBus bus, IStartupSweepCoordinator sweep)
    {
        _registry = registry;
        _git = git;
        _bus = bus;
        _sweep = sweep;
    }

    public void Start(IUiDispatcher dispatcher)
    {
        if (_dispatcher != null) return; // idempotent
        _dispatcher = dispatcher;
        _refsSub = _bus.SubscribeScoped<RefsChangedMessage>(m => Refresh(m.RepoId));
        // Subscribe fires Reset immediately with the current list, seeding a snapshot per primary.
        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
    }

    /// <summary>
    /// All primaries in <paramref name="repoId"/>'s group that carry a local branch named
    /// <paramref name="branchName"/> — including <paramref name="repoId"/> itself — when it forms a
    /// set (two or more members, the name not being any member's own default). Empty when it is not
    /// a synced branch. Members are ordered by the group's membership order. Read on right-click, so
    /// it queries the current snapshots directly rather than being observable.
    /// </summary>
    public IReadOnlyList<Guid> SyncedReposFor(Guid repoId, string branchName)
    {
        var group = _registry.FindGroupContaining(repoId);
        if (group == null) return Array.Empty<Guid>();
        foreach (var set in Correlate(group))
            if (string.Equals(set.BranchName, branchName, StringComparison.Ordinal) && set.RepoIds.Contains(repoId))
                return set.RepoIds;
        return Array.Empty<Guid>();
    }

    /// <summary>
    /// Every change set in a group: same-named local branches shared by two or more of its primaries.
    /// </summary>
    public IReadOnlyList<SyncedBranch> SetsForGroup(Guid groupId)
    {
        foreach (var group in _registry.Groups)
            if (group.Id == groupId) return Correlate(group);
        return Array.Empty<SyncedBranch>();
    }

    private IReadOnlyList<SyncedBranch> Correlate(Group group)
    {
        var ordered = new List<Guid>();
        foreach (var id in group.RepoIds)
            if (_snapshots.ContainsKey(id)) ordered.Add(id);
        return SyncedBranchCorrelator.Correlate(ordered, _snapshots);
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                // Defer the startup sweep behind the active repo's first load, like the other sweeps.
                _sweep.RunInitialSweep(() =>
                {
                    foreach (var r in _registry.Repos)
                        if (r.IsPrimary) Refresh(r.Id);
                });
                break;
            case ListChangeKind.Added:
                if (change.Item is { IsPrimary: true } added) Refresh(added.Id);
                break;
            case ListChangeKind.Replaced:
                if (change.Item is { IsPrimary: true } replaced) Refresh(replaced.Id);
                break;
            case ListChangeKind.Removed:
                if (change.OldItem is { } removed && _snapshots.Remove(removed.Id))
                    _revision.Value++;
                break;
            // Moved / Cleared: membership shifts don't change a repo's own branches; Correlate reads
            // live group membership on the next query, and the menu rebuilds on open.
        }
    }

    private void Refresh(Guid repoId)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        var repo = FindPrimary(repoId);
        if (repo == null) return; // worktree/submodule refs re-broadcasts are ignored — sets are between primaries
        var gen = (_epoch.TryGetValue(repoId, out var e) ? e : 0) + 1;
        _epoch[repoId] = gen;
        _sweep.RunThrottled(() =>
        {
            RepoBranchSnapshot? snapshot;
            try
            {
                snapshot = _git.GetBranches(repo) is Fetched<BranchListing>.Ok ok
                    ? new RepoBranchSnapshot(
                        _git.GetDefaultBranchName(repo),
                        ok.Value.LocalBranches.Select(b => b.Name).ToList())
                    : null;
            }
            catch { snapshot = null; }
            dispatcher.Post(() =>
            {
                if (_disposed) return;
                // Drop a result superseded by a newer refresh for the same repo.
                if (_epoch.TryGetValue(repoId, out var cur) && cur != gen) return;
                if (snapshot == null) return; // a failed probe keeps the last known snapshot
                _snapshots[repoId] = snapshot;
                _revision.Value++;
            });
        });
    }

    private Repo? FindPrimary(Guid id)
    {
        foreach (var r in _registry.Repos)
            if (r.Id == id) return r.IsPrimary ? r : null;
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        _reposSub?.Dispose();
        _refsSub?.Dispose();
        _revision.Dispose();
    }
}
