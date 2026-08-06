using GitBench.Git;
using ZGF.Gui;

namespace GitBench.Features.Repos;

/// <summary>
/// Tells the shared read gate which repo the user is looking at, so that repo's reads are admitted
/// ahead of every other repo's. The gate can't read the registry itself: it would point GitBench.Git
/// at Features.Repos, and <see cref="IRepoRegistry.Active"/> is dependency-tracked, so reading it
/// from a gate waiter's thread would register a spurious dependency in whatever Derived happens to be
/// evaluating. So the id is pushed in from here, on the UI thread.
/// </summary>
internal sealed class GitReadPriorityService : IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitReadGate _gate;
    private IDisposable? _activeSub;

    public GitReadPriorityService(IRepoRegistry registry, IGitReadGate gate)
    {
        _registry = registry;
        _gate = gate;
    }

    public void Start()
        => _activeSub ??= _registry.Active.Subscribe(repo => _gate.SetForegroundRepo(repo?.Id));

    public void Dispose() => _activeSub?.Dispose();
}
