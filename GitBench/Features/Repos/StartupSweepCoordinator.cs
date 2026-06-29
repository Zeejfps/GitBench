namespace GitBench.Features.Repos;

/// <summary>
/// Sequences the all-repos background sweeps (per-repo status probes, worktree/submodule
/// discovery) against the active repo's first load. Each of those services fans out a git
/// process per repo the moment the registry first populates; run concurrently with the active
/// repo's heavy first load they contend for the disk and slow the one thing the user is waiting
/// for. This holds the initial sweeps until that first load has landed, then releases them under
/// a shared concurrency cap so the deferred burst can't saturate the disk all at once.
/// </summary>
internal interface IStartupSweepCoordinator
{
    // Runs an initial all-repos sweep, deferred until the active repo's first load lands. Before
    // then it is queued; after, it runs synchronously on the caller. Call on the UI thread.
    void RunInitialSweep(Action sweep);

    // Runs one unit of sweep git work off the UI thread under a shared concurrency cap.
    void RunThrottled(Action work);

    // Releases the queued initial sweeps — called once the active repo's first load lands (or
    // when there is no active repo to wait on). Only the first call releases; the rest are no-ops.
    void MarkActiveReady();
}

internal sealed class StartupSweepCoordinator : IStartupSweepCoordinator, IDisposable
{
    // Small enough that a many-repo startup sweep can't burst one git process per repo at once,
    // large enough to keep interactive (post-startup) syncs responsive.
    private const int MaxConcurrentSweeps = 4;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _throttle = new(MaxConcurrentSweeps);
    private bool _ready;
    private List<Action>? _pending = new();

    public void RunInitialSweep(Action sweep)
    {
        lock (_lock)
        {
            if (!_ready)
            {
                (_pending ??= new()).Add(sweep);
                return;
            }
        }
        sweep();
    }

    public void RunThrottled(Action work)
    {
        Task.Run(async () =>
        {
            await _throttle.WaitAsync().ConfigureAwait(false);
            try { work(); }
            finally { _throttle.Release(); }
        });
    }

    public void MarkActiveReady()
    {
        List<Action>? toRun;
        lock (_lock)
        {
            if (_ready) return;
            _ready = true;
            toRun = _pending;
            _pending = null;
        }
        if (toRun == null) return;
        foreach (var sweep in toRun) sweep();
    }

    public void Dispose() => _throttle.Dispose();
}
