using ZGF.Observable;

namespace GitGui;

// Owns one RepoWatcher per known repo. Subscribes to the repo registry's list and
// creates/disposes watchers as repos are added or removed. Registered as a service
// at startup; lifetime is the lifetime of the app.
internal sealed class RepoWatcherService : IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IRepoActivityTracker _activity;
    private readonly Dictionary<Guid, RepoWatcher> _watchers = new();
    private readonly IDisposable _reposSub;

    public RepoWatcherService(
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoActivityTracker activity)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _bus = bus;
        _activity = activity;

        // Subscribe fires Reset immediately with the current list contents, so we don't
        // need a separate initial-seed loop.
        _reposSub = _registry.Repos.Subscribe(OnRepoListChange);
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                DisposeAll();
                foreach (var repo in _registry.Repos)
                    Start(repo);
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added) Start(added);
                break;
            case ListChangeKind.Removed:
                if (change.OldItem is { } removed) Stop(removed.Id);
                break;
            case ListChangeKind.Replaced:
                if (change.OldItem is { } oldRepo) Stop(oldRepo.Id);
                if (change.Item is { } newRepo) Start(newRepo);
                break;
            case ListChangeKind.Moved:
                // No-op: reordering doesn't change which repos exist.
                break;
            case ListChangeKind.Cleared:
                DisposeAll();
                break;
        }
    }

    private void Start(Repo repo)
    {
        if (_watchers.ContainsKey(repo.Id)) return;
        try
        {
            _watchers[repo.Id] = new RepoWatcher(repo, _dispatcher, _bus, _activity);
        }
        catch
        {
            // RepoWatcher already swallows FSW construction failures internally; a throw
            // here would be exceptional. Don't let it kill the registry subscription.
        }
    }

    private void Stop(Guid repoId)
    {
        if (_watchers.Remove(repoId, out var w))
            w.Dispose();
    }

    private void DisposeAll()
    {
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        _reposSub.Dispose();
        DisposeAll();
    }
}
