using GitBench.Git;

namespace GitBench.Features.FileBrowser;

/// <summary>One repository's files, as git lists them: tracked, plus untracked and not ignored.</summary>
/// <remarks>
/// Ignored files are left out even though the tree beside this lists them, because the two are asked
/// different questions. The tree is asked what is in this directory, where a build output is an
/// answer; this is asked which file you meant, where forty thousand of them are not.
/// </remarks>
internal sealed class GitFileCatalog : IFileCatalog
{
    private readonly IGitRepositoryReader _git;
    private readonly Repo _repo;

    public GitFileCatalog(IGitRepositoryReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public IReadOnlyList<string> List() => _git.ListWorkingTreeFiles(_repo);
}
