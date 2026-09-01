using System.Collections.Concurrent;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Owns one RepoWatcher per known repo. Subscribes to the repo registry's list and
// creates/disposes watchers as repos are added or removed. Registered as a service
// at startup; lifetime is the lifetime of the app.
internal sealed class RepoWatcherService : IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMessageBus _bus;
    private readonly IRepoActivityTracker _activity;
    private readonly IGitReadGate _readGate;
    private readonly Dictionary<Guid, RepoWatcher> _watchers = new();
    private readonly Dictionary<Guid, int> _attaching = new();
    private readonly ConcurrentQueue<(Repo Repo, int Attempt)> _queue = new();
    private int _draining;
    private int _nextAttempt;
    private bool _disposed;
    private IDisposable? _reposSub;

    public RepoWatcherService(
        IRepoRegistry registry,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        IRepoActivityTracker activity,
        IGitReadGate readGate)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _bus = bus;
        _activity = activity;
        _readGate = readGate;
    }

    public void Start()
    {
        _dispatcher.Post(() =>
        {
            if (!_disposed) _reposSub ??= _registry.Repos.Subscribe(OnRepoListChange);
        });
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Reset:
                DisposeAll();
                foreach (var repo in _registry.Repos)
                    StartWatching(repo);
                break;
            case ListChangeKind.Added:
                if (change.Item is { } added) StartWatching(added);
                break;
            case ListChangeKind.Removed:
                if (change.OldItem is { } removed) Stop(removed.Id);
                break;
            case ListChangeKind.Replaced:
                if (change.OldItem is { } oldRepo) Stop(oldRepo.Id);
                if (change.Item is { } newRepo) StartWatching(newRepo);
                break;
            case ListChangeKind.Moved:
                // No-op: reordering doesn't change which repos exist.
                break;
            case ListChangeKind.Cleared:
                DisposeAll();
                break;
        }
    }

    private void StartWatching(Repo repo)
    {
        if (_watchers.ContainsKey(repo.Id)) return;

        var attempt = ++_nextAttempt;
        _attaching[repo.Id] = attempt;
        _queue.Enqueue((repo, attempt));
        if (Interlocked.Exchange(ref _draining, 1) == 0) Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var item))
            {
                RepoWatcher? watcher = null;
                try
                {
                    watcher = new RepoWatcher(item.Repo, _dispatcher, _bus, _activity, _readGate);
                }
                catch
                {
                    // RepoWatcher reports and absorbs FSW construction failures internally; a throw
                    // here would be exceptional. Don't let it kill the registry subscription.
                }
                var landed = item;
                _dispatcher.Post(() => Attached(landed.Repo.Id, landed.Attempt, watcher));
            }

            Interlocked.Exchange(ref _draining, 0);
            if (_queue.IsEmpty) return;
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
        }
    }

    private void Attached(Guid repoId, int attempt, RepoWatcher? watcher)
    {
        var current = _attaching.TryGetValue(repoId, out var owner) && owner == attempt;
        if (current) _attaching.Remove(repoId);
        if (watcher == null) return;

        if (current && !_disposed && !_watchers.ContainsKey(repoId)) _watchers[repoId] = watcher;
        else watcher.Dispose();
    }

    private void Stop(Guid repoId)
    {
        _attaching.Remove(repoId);
        if (_watchers.Remove(repoId, out var w))
            w.Dispose();
    }

    private void DisposeAll()
    {
        _attaching.Clear();
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        _reposSub?.Dispose();
        DisposeAll();
    }
}
