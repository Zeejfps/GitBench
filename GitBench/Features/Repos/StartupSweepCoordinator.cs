using GitBench.Git;

namespace GitBench.Features.Repos;

/// <summary>
/// Runs one unit of per-repo discovery off the UI thread under the shared
/// <see cref="IGitReadGate"/>, so worktree and submodule syncs never contend with the reads the
/// active repo is waiting on.
/// </summary>
internal interface IStartupSweepCoordinator
{
    void RunThrottled(Guid repoId, Action work);
}

internal sealed class StartupSweepCoordinator : IStartupSweepCoordinator
{
    private readonly IGitReadGate _gate;

    public StartupSweepCoordinator(IGitReadGate gate) => _gate = gate;

    public void RunThrottled(Guid repoId, Action work)
    {
        Task.Run(async () =>
        {
            using (await _gate.Acquire(repoId, GitReadKind.Discovery))
                work();
        });
    }
}
