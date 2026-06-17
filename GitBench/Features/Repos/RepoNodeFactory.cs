using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;

namespace GitBench.Features.Repos;

/// <summary>
/// Builds <see cref="RepoNodeViewModel"/>s, holding the services every node needs so the recursive
/// tree (primary → worktrees/submodules → …) can spawn its own children without reaching back into
/// a build <see cref="ZGF.Gui.Context"/>.
/// </summary>
internal sealed class RepoNodeFactory
{
    private readonly IRepoRegistry _registry;
    private readonly IRepoStatusStore _status;
    private readonly IMessageBus _bus;
    private readonly IGitService _git;
    private readonly IPlatformShell? _shell;

    public RepoNodeFactory(
        IRepoRegistry registry,
        IRepoStatusStore status,
        IMessageBus bus,
        IGitService git,
        IPlatformShell? shell)
    {
        _registry = registry;
        _status = status;
        _bus = bus;
        _git = git;
        _shell = shell;
    }

    public RepoNodeViewModel Create(Repo repo, int depth) =>
        new(repo, depth, _registry, _status, _bus, _git, _shell, this);
}
