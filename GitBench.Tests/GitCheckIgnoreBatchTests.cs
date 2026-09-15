using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

public sealed class GitCheckIgnoreBatchTests : IDisposable
{
    private readonly TempGitRepo _work = TempGitRepo.Init();
    private readonly GitService _git;
    private readonly Repo _repo;

    public GitCheckIgnoreBatchTests()
    {
        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _work.Path, "test");

        File.WriteAllText(Path.Combine(_work.Path, ".gitignore"), "build/\n*.log\n");
    }

    public void Dispose()
    {
        _work.Dispose();
    }

    [Fact]
    public void ReturnsOnlyThePathsTheRulesMatch()
    {
        var ignored = _git.IsPathIgnored(_repo, ["build/", "src/", "app.log", "README.md"]);

        Assert.Equal(["app.log", "build/"], ignored.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void NothingMatchingIsAnAnswerNotAFailure()
    {
        var ignored = _git.IsPathIgnored(_repo, ["src/", "README.md"]);

        Assert.Empty(ignored);
    }

    [Fact]
    public void EverythingUnderAnIgnoredDirectoryIsReportedIgnored()
    {
        var ignored = _git.IsPathIgnored(_repo, ["build/out/thing.o"]);

        Assert.Equal(["build/out/thing.o"], ignored.ToArray());
    }

    [Fact]
    public void APathWithANewlineInItIsAskedAboutAsOnePath()
    {
        const string awkward = "we\nird.log";

        var ignored = _git.IsPathIgnored(_repo, [awkward, "README.md"]);

        Assert.Equal([awkward], ignored.ToArray());
    }

    [Fact]
    public void ABlankLineInACrlfIgnoreFileIsNotARule()
    {
        File.WriteAllText(Path.Combine(_work.Path, ".gitignore"), "build/\r\n\r\nsrc/nested/\r\n");

        var ignored = _git.IsPathIgnored(_repo, ["src/", "build/", "app.log"]);

        Assert.Equal(["build/"], ignored.ToArray());
    }

    [Fact]
    public void APathTheRulesReAdmitIsNotIgnored()
    {
        File.WriteAllText(Path.Combine(_work.Path, ".gitignore"), "*.log\n!keep.log\n");

        var ignored = _git.IsPathIgnored(_repo, ["app.log", "keep.log"]);

        Assert.Equal(["app.log"], ignored.ToArray());
    }

    [Fact]
    public void AnEmptyBatchIsAnsweredWithoutAskingGit()
    {
        Assert.Empty(_git.IsPathIgnored(_repo, []));
    }
}
