using ZGF.Observable;

namespace GitGui;

// Owns the discovery-side-effect for worktrees: shells out to `git worktree list`
// and reconciles the result into IRepoRegistry. The registry stays pure data; this
// service is the only place that turns filesystem state into Repo records.
//
// Triggers:
//   * A primary repo is added to the registry (or appears on startup).
//   * WorktreesChangedMessage(primaryId) fires (from RepoWatcher detecting a change
//     under <primary>/.git/worktrees/, or from an in-app add/remove dialog).
//   * RefsChangedMessage(primaryId) — re-broadcast as RefsChangedMessage(worktreeId)
//     for each child, because the worktrees share refs/heads with the primary and
//     their BranchesView needs the same refresh.
internal sealed class WorktreeSyncService : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IDisposable _reposSub;
    private readonly IDisposable _worktreesChangedSub;
    private readonly IDisposable _refsChangedSub;

    public WorktreeSyncService(
        IRepoRegistry registry,
        IGitService git,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _registry = registry;
        _git = git;
        _dispatcher = dispatcher;
        _bus = bus;

        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
        _worktreesChangedSub = _bus.SubscribeScoped<WorktreesChangedMessage>(OnWorktreesChanged);
        _refsChangedSub = _bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged);
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                foreach (var repo in _registry.Repos)
                {
                    if (repo.IsPrimary) ScheduleSync(repo.Id);
                }
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added && added.IsPrimary)
                    ScheduleSync(added.Id);
                break;
        }
    }

    private void OnWorktreesChanged(WorktreesChangedMessage msg) => ScheduleSync(msg.PrimaryRepoId);

    // Worktrees share refs/heads with their primary, so a branch update visible to one
    // is visible to all. The primary's RepoWatcher fires RefsChangedMessage(primaryId);
    // we fan out an extra RefsChangedMessage per child so worktree BranchesView refreshes.
    // We also re-sync the worktree set on refs changes because HEAD swaps in any worktree
    // (which don't add/remove worktrees) still change which branch is "taken where".
    private void OnRefsChanged(RefsChangedMessage msg)
    {
        Repo? source = null;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == msg.RepoId) { source = r; break; }
        }
        if (source is null) return;

        var primaryId = source.ParentRepoId ?? source.Id;
        ScheduleSync(primaryId);

        if (source.IsPrimary)
        {
            foreach (var wt in _registry.GetWorktrees(source.Id))
            {
                var id = wt.Id;
                _bus.Broadcast(new RefsChangedMessage(id));
            }
        }
    }

    private void ScheduleSync(Guid primaryId)
    {
        // The discovery shells out to git and parses output; run it off the UI thread
        // and post the registry mutation back. The registry itself is not thread-safe
        // for writes from arbitrary threads.
        Task.Run(() =>
        {
            Repo? primary = null;
            foreach (var r in _registry.Repos)
            {
                if (r.Id == primaryId) { primary = r; break; }
            }
            if (primary is null || !primary.IsPrimary) return;

            var infos = _git.ListWorktrees(primary, out _);
            var primaryNormalized = TryFullPath(primary.Path);

            string? primaryBranch = null;
            var descriptors = new List<WorktreeDescriptor>(infos.Count);
            foreach (var info in infos)
            {
                if (info.IsBare) continue;
                var normalized = TryFullPath(info.Path);
                if (string.Equals(normalized, primaryNormalized, PathCmp))
                {
                    primaryBranch = info.Branch;
                    continue;
                }

                // Display label: prefer branch name, else last path segment.
                var display = !string.IsNullOrEmpty(info.Branch)
                    ? info.Branch!
                    : Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                descriptors.Add(new WorktreeDescriptor(normalized, display, info.Branch));
            }

            _dispatcher.Post(() =>
            {
                _registry.SetPrimaryBranch(primaryId, primaryBranch);
                _registry.ReplaceWorktreesFor(primaryId, descriptors);
            });
        });
    }

    private static string TryFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose()
    {
        _reposSub.Dispose();
        _worktreesChangedSub.Dispose();
        _refsChangedSub.Dispose();
    }
}
