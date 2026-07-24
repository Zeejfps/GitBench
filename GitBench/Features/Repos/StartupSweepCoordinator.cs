using GitBench.Git;

namespace GitBench.Features.Repos;

/// <summary>
/// Sequences the all-repos background sweeps (per-repo status probes, worktree/submodule
/// discovery) against the active repo's first load. Each of those services fans out a git
/// process per repo the moment the registry first populates; run concurrently with the active
/// repo's heavy first load they contend for the disk and slow the one thing the user is waiting
/// for. This holds the initial sweeps until that first load has landed, then releases them; the
/// deferred burst is bounded by the shared <see cref="IGitReadGate"/> the discovery reads run under.
/// </summary>
internal interface IStartupSweepCoordinator
{
    // Runs an initial all-repos sweep, deferred until the active repo's first load lands. Before
    // then it is queued; after, it runs synchronously on the caller. Call on the UI thread.
    void RunInitialSweep(Action sweep);

    // Runs one unit of sweep git work off the UI thread under the shared read gate.
    void RunThrottled(Guid repoId, Action work);

    // Releases the queued initial sweeps — called once the active repo's first load lands (or
    // when there is no active repo to wait on). Only the first call releases; the rest are no-ops.
    void MarkActiveReady();
}

internal sealed class StartupSweepCoordinator : IStartupSweepCoordinator
{
    private readonly IGitReadGate _gate;
    private readonly object _lock = new();
    private bool _ready;
    private List<Action>? _pending = new();

    public StartupSweepCoordinator(IGitReadGate gate) => _gate = gate;

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

    public void RunThrottled(Guid repoId, Action work)
    {
        Task.Run(async () =>
        {
            using (await _gate.Acquire(repoId, GitReadKind.Discovery))
                work();
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
}
