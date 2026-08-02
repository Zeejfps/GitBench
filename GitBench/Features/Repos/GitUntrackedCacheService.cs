using GitBench.Git;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Applies the opt-in core.untrackedCache setting to the repos GitBench manages. While the
// preference is on it writes core.untrackedCache=true into each primary repo's --local config —
// on the repos already open when it's flipped on, and on each one opened afterwards. The write
// itself is idempotent and respectful (see GitService.ApplyUntrackedCache), so re-registration is
// a no-op after the first pass. Worktrees and submodules are skipped: the config bool lives in the
// family's shared config, so setting it once on the primary covers every linked worktree's status.
internal sealed class GitUntrackedCacheService : IHostedService, IDisposable
{
    private readonly IRepoRegistry _registry;
    private readonly IGitConfigOperations _git;
    private readonly State<bool> _enabled;
    private IDisposable? _enabledSub;
    private IDisposable? _reposSub;

    public GitUntrackedCacheService(IRepoRegistry registry, IGitConfigOperations git, State<bool> enabled)
    {
        _registry = registry;
        _git = git;
        _enabled = enabled;
    }

    public void Start()
    {
        // The preference observable drives the bulk pass (fires immediately with the current value,
        // covering both "on at startup" and a later flip-on); the repo list drives the incremental
        // case for a repo opened while the preference is already on.
        _enabledSub ??= _enabled.Subscribe(OnEnabledChanged);
        _reposSub ??= _registry.Repos.Subscribe(OnRepoListChange);
    }

    private void OnEnabledChanged(bool enabled)
    {
        if (!enabled) return;
        foreach (var repo in _registry.Repos)
            if (repo.IsPrimary) Apply(repo);
    }

    private void OnRepoListChange(ListChange<Repo> change)
    {
        // The bulk pass is owned by OnEnabledChanged, so only the incremental add matters here; the
        // opening Reset would just re-apply what OnEnabledChanged already covered.
        if (change.Kind == ListChangeKind.Added && _enabled.Value &&
            change.Item is { IsPrimary: true } added)
            Apply(added);
    }

    // Fire-and-forget on a background thread and non-fatal: this is an optimization, not a user
    // action, so a failed or slow config write is logged and never blocks a repo open or raises a
    // dialog.
    private void Apply(Repo repo)
        => Task.Run(() =>
        {
            try
            {
                var outcome = _git.ApplyUntrackedCache(repo);
                if (outcome is GitOutcome.Failed failed)
                    Console.WriteLine($"Failed to apply untracked cache to {repo.Path}: {failed.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply untracked cache to {repo.Path}: {ex.Message}");
            }
        });

    public void Dispose()
    {
        _enabledSub?.Dispose();
        _reposSub?.Dispose();
    }
}
