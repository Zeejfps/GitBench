using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;

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
    private readonly ILocalizationService _loc;
    private readonly IClipboard? _clipboard;

    public RepoNodeFactory(
        IRepoRegistry registry,
        IRepoStatusStore status,
        IMessageBus bus,
        IGitService git,
        IPlatformShell? shell,
        ILocalizationService loc,
        IClipboard? clipboard)
    {
        _registry = registry;
        _status = status;
        _bus = bus;
        _git = git;
        _shell = shell;
        _loc = loc;
        _clipboard = clipboard;
    }

    public RepoNodeViewModel Create(Repo repo, int depth) =>
        new(repo, depth, _registry, _status, _bus, _git, _shell, _loc, _clipboard, this);
}
