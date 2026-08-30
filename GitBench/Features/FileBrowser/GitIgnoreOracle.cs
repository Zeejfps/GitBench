using GitBench.Git;

namespace GitBench.Features.FileBrowser;

/// <summary>One repository's ignore rules, asked a directory at a time.</summary>
internal sealed class GitIgnoreOracle : IIgnoreOracle
{
    private readonly IGitRepositoryReader _git;
    private readonly Repo _repo;

    public GitIgnoreOracle(IGitRepositoryReader git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public IReadOnlySet<string> Ignored(IReadOnlyList<string> relativePaths) =>
        _git.IsPathIgnored(_repo, relativePaths);
}
