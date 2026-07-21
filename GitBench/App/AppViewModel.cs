using GitBench.Features.Repos;
using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// Root view model for <see cref="AppWidget"/>. Holds the app-shell state that isn't owned by any
/// one feature — currently just whether any repository is open, which decides between the workspace
/// and the full-window welcome screen.
/// </summary>
internal sealed class AppViewModel : IDisposable
{
    private readonly Derived<bool> _hasRepos;

    public IReadable<bool> HasRepos => _hasRepos;

    public AppViewModel(IRepoRegistry registry)
    {
        _hasRepos = new Derived<bool>(() => registry.Repos.Any());
    }

    public void Dispose() => _hasRepos.Dispose();
}
