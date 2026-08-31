using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// `git init` from the "New Repository" menu entry: the folder the picker returns has to come back
// as something RepoRegistry will actually open, whether it exists yet or not.
public sealed class GitInitTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-init-");
    private readonly GitService _git = new(new NullActivityTracker());

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Init_makes_an_empty_folder_a_repository()
    {
        var path = Path.Combine(_dir.Path, "fresh");
        Directory.CreateDirectory(path);

        Assert.IsType<GitOutcome.Success>(_git.Init(path));
        Assert.True(RepoStateStore.IsGitRepo(path));
    }

    [Fact]
    public void Init_creates_a_folder_that_does_not_exist_yet()
    {
        var path = Path.Combine(_dir.Path, "not-yet", "nested");

        Assert.IsType<GitOutcome.Success>(_git.Init(path));
        Assert.True(RepoStateStore.IsGitRepo(path));
    }

    [Fact]
    public void Init_on_an_existing_repository_succeeds_and_keeps_its_history()
    {
        var path = Path.Combine(_dir.Path, "twice");
        Assert.IsType<GitOutcome.Success>(_git.Init(path));
        var headBefore = File.ReadAllText(Path.Combine(path, ".git", "HEAD"));

        Assert.IsType<GitOutcome.Success>(_git.Init(path));
        Assert.Equal(headBefore, File.ReadAllText(Path.Combine(path, ".git", "HEAD")));
    }

    [Fact]
    public void Init_fails_when_the_path_is_a_file()
    {
        var path = Path.Combine(_dir.Path, "a-file.txt");
        File.WriteAllText(path, "not a folder");

        var failed = Assert.IsType<GitOutcome.Failed>(_git.Init(path));
        Assert.NotEmpty(failed.Message);
    }

    [Fact]
    public void Init_fails_on_a_blank_path()
        => Assert.IsType<GitOutcome.Failed>(_git.Init("   "));
}
