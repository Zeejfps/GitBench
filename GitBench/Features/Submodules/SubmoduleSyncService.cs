using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

// Mirrors WorktreeSyncService for submodules: shells out to git, builds descriptors,
// and reconciles into IRepoRegistry. The registry stays pure data; this service is the
// only place that turns filesystem state (.gitmodules entries + per-submodule .git
// status) into Repo records.
//
// A submodule host is any top-level checkout — a primary or a worktree (worktrees share the
// primary's .gitmodules). The host id flows through every trigger below.
//
// Triggers:
//   * A primary or worktree is added (or appears on startup).
//   * SubmodulesChangedMessage(hostId) — from RepoWatcher detecting `.gitmodules`
//     or `.git/modules/<name>/` changes, or from a dialog presenter after an add /
//     update / deinit.
//   * RefsChangedMessage(hostId) — HEAD moves can swap in a different .gitmodules,
//     which changes the submodule set even though the working tree didn't change.
internal sealed class SubmoduleSyncService : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IStartupSweepCoordinator _sweep;
    private readonly IDisposable _reposSub;
    private readonly IDisposable _submodulesChangedSub;
    private readonly IDisposable _refsChangedSub;

    public SubmoduleSyncService(
        IRepoRegistry registry,
        IGitService git,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IStartupSweepCoordinator sweep)
    {
        _registry = registry;
        _git = git;
        _dispatcher = dispatcher;
        _bus = bus;
        _sweep = sweep;

        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
        _submodulesChangedSub = _bus.SubscribeScoped<SubmodulesChangedMessage>(OnSubmodulesChanged);
        _refsChangedSub = _bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged);
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                // Defer the startup discovery burst until the active repo's first load has landed.
                _sweep.RunInitialSweep(() =>
                {
                    foreach (var repo in _registry.Repos)
                        if (repo.IsPrimary || repo.IsWorktree) ScheduleSync(repo.Id);
                });
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added && (added.IsPrimary || added.IsWorktree))
                    ScheduleSync(added.Id);
                break;
        }
    }

    private void OnSubmodulesChanged(SubmodulesChangedMessage msg) => ScheduleSync(msg.PrimaryRepoId);

    private void OnRefsChanged(RefsChangedMessage msg)
    {
        Repo? source = null;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == msg.RepoId) { source = r; break; }
        }
        if (source is null) return;

        // Only top-level checkouts host submodules; a worktree's own HEAD re-syncs itself.
        if (!source.IsPrimary && !source.IsWorktree) return;

        ScheduleSync(source.Id);
    }

    // Submodules can themselves contain submodules; we walk the whole tree but cap the depth
    // as a backstop against pathological/cyclic nesting (the visited-path set is the real guard).
    private const int MaxSubmoduleDepth = 8;

    private void ScheduleSync(Guid hostId)
    {
        _sweep.RunThrottled(() =>
        {
            Repo? host = null;
            foreach (var r in _registry.Repos)
            {
                if (r.Id == hostId) { host = r; break; }
            }
            if (host is null || (!host.IsPrimary && !host.IsWorktree)) return;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeForVisit(host.Path) };
            var roots = EnumerateSubmoduleTree(host.Path, 0, visited);

            _dispatcher.Post(() => _registry.ReplaceSubmoduleForest(hostId, roots));
        });
    }

    // Recursively reads `git submodule` for repoPath, building a descriptor tree. Descends into
    // each initialized submodule's working tree (a submodule's path is itself a git repo), which
    // is what surfaces submodules-of-submodules. ListSubmodules returns empty for a non-repo path,
    // so this terminates naturally at the leaves.
    private List<SubmoduleNode> EnumerateSubmoduleTree(string repoPath, int depth, HashSet<string> visited)
    {
        if (depth >= MaxSubmoduleDepth) return new List<SubmoduleNode>();

        // ListSubmodules only reads repo.Path, so a throwaway Repo standing in for this level is fine.
        var probe = new Repo(Guid.NewGuid(), repoPath, System.IO.Path.GetFileName(repoPath.TrimEnd('/', '\\')));
        var infos = _git.ListSubmodules(probe);

        var nodes = new List<SubmoduleNode>(infos.Count);
        foreach (var info in infos)
        {
            // Display label: prefer the last path segment (matches `git status`'s identifier).
            // Submodules don't have a single canonical "branch" the way a worktree does, so
            // Branch is left at its tracked-branch hint (or null).
            var rel = info.Path.TrimEnd('/');
            var display = System.IO.Path.GetFileName(rel);
            if (string.IsNullOrEmpty(display)) display = rel;

            // Only descend into a checked-out submodule — an uninitialized one has no working
            // tree to read .gitmodules from. visited.Add breaks symlink/path cycles.
            var children = new List<SubmoduleNode>();
            if (info.Status != SubmoduleStatus.NotInitialized && visited.Add(NormalizeForVisit(info.AbsolutePath)))
                children = EnumerateSubmoduleTree(info.AbsolutePath, depth + 1, visited);

            nodes.Add(new SubmoduleNode(new SubmoduleDescriptor(info.AbsolutePath, display, info.Branch), children));
        }
        return nodes;
    }

    private static string NormalizeForVisit(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }
    }

    public void Dispose()
    {
        _reposSub.Dispose();
        _submodulesChangedSub.Dispose();
        _refsChangedSub.Dispose();
    }
}
