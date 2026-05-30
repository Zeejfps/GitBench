using ZGF.Observable;

namespace GitGui;

// Mirrors WorktreeSyncService for submodules: shells out to git, builds descriptors,
// and reconciles into IRepoRegistry. The registry stays pure data; this service is the
// only place that turns filesystem state (.gitmodules entries + per-submodule .git
// status) into Repo records.
//
// Triggers:
//   * A primary repo is added (or appears on startup).
//   * SubmodulesChangedMessage(primaryId) — from RepoWatcher detecting `.gitmodules`
//     or `.git/modules/<name>/` changes, or from a dialog presenter after an add /
//     update / deinit.
//   * RefsChangedMessage(primaryId) — HEAD moves can swap in a different .gitmodules,
//     which changes the submodule set even though the working tree didn't change.
internal sealed class SubmoduleSyncService : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IDisposable _reposSub;
    private readonly IDisposable _submodulesChangedSub;
    private readonly IDisposable _refsChangedSub;

    public SubmoduleSyncService(
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
        _submodulesChangedSub = _bus.SubscribeScoped<SubmodulesChangedMessage>(OnSubmodulesChanged);
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

    private void OnSubmodulesChanged(SubmodulesChangedMessage msg) => ScheduleSync(msg.PrimaryRepoId);

    private void OnRefsChanged(RefsChangedMessage msg)
    {
        Repo? source = null;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == msg.RepoId) { source = r; break; }
        }
        if (source is null) return;

        // Only primaries (and their worktrees, which share refs) carry submodules; a
        // submodule's own RefsChangedMessage doesn't affect the parent's submodule set.
        if (source.IsSubmodule) return;

        var primaryId = source.ParentRepoId ?? source.Id;
        ScheduleSync(primaryId);
    }

    private void ScheduleSync(Guid primaryId)
    {
        Task.Run(() =>
        {
            Repo? primary = null;
            foreach (var r in _registry.Repos)
            {
                if (r.Id == primaryId) { primary = r; break; }
            }
            if (primary is null || !primary.IsPrimary) return;

            var infos = _git.ListSubmodules(primary, out _);
            var descriptors = new List<SubmoduleDescriptor>(infos.Count);
            foreach (var info in infos)
            {
                // Display label: prefer the last path segment (matches `git status`'s
                // identifier). Submodules don't have a single canonical "branch" the way
                // a worktree does, so Branch is left at its tracked-branch hint (or null).
                var rel = info.Path.TrimEnd('/');
                var display = System.IO.Path.GetFileName(rel);
                if (string.IsNullOrEmpty(display)) display = rel;
                descriptors.Add(new SubmoduleDescriptor(info.AbsolutePath, display, info.Branch));
            }

            _dispatcher.Post(() => _registry.ReplaceSubmodulesFor(primaryId, descriptors));
        });
    }

    public void Dispose()
    {
        _reposSub.Dispose();
        _submodulesChangedSub.Dispose();
        _refsChangedSub.Dispose();
    }
}
