using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

// Keeps the parent's recorded submodule pointer in sync with where the submodule is actually
// checked out. When a submodule's HEAD moves — the user checks out a branch/commit inside it,
// resets it, etc. — git would otherwise leave it as an unstaged "modified" entry in the parent
// until a manual `git add`. Instead we stage that pointer update automatically, so moving the
// submodule "just works": the parent's gitlink follows the submodule, ready to commit.
//
// Trigger: RefsChangedMessage(submoduleId). Every submodule is its own Repo in the registry with
// its own RepoWatcher, so this fires for both in-app operations and external HEAD moves. The
// staging `git add` runs through the git runner, whose writes the parent's RepoWatcher ignores,
// so there's no watcher feedback loop; and since `add` doesn't move refs it can't re-trigger us.
internal sealed class SubmodulePointerSyncService : IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitService _git;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private IDisposable? _refsChangedSub;

    public SubmodulePointerSyncService(
        IRepoRegistry registry,
        IGitService git,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _registry = registry;
        _git = git;
        _dispatcher = dispatcher;
        _bus = bus;
    }

    public void Start()
    {
        _refsChangedSub ??= _bus.SubscribeScoped<RefsChangedMessage>(OnRefsChanged);
    }

    private void OnRefsChanged(RefsChangedMessage msg)
    {
        Repo? submodule = null;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == msg.RepoId) { submodule = r; break; }
        }
        if (submodule is not { IsSubmodule: true } sub || sub.ParentRepoId is not { } parentId) return;

        Repo? parent = null;
        foreach (var r in _registry.Repos)
        {
            if (r.Id == parentId) { parent = r; break; }
        }
        if (parent is null) return;

        var parentRepo = parent;
        Task.Run(() =>
        {
            string relativePath;
            try { relativePath = System.IO.Path.GetRelativePath(parentRepo.Path, sub.Path); }
            catch { return; }
            if (string.IsNullOrEmpty(relativePath) || relativePath == ".") return;

            if (_git.StageSubmodulePointer(parentRepo, relativePath))
                _dispatcher.Post(() => _bus.Broadcast(new WorkingTreeChangedMessage(parentRepo.Id)));
        });
    }

    public void Dispose() => _refsChangedSub?.Dispose();
}
