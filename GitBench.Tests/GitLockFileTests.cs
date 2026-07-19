using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

public sealed class GitLockFileTests
{
    [Fact]
    public void DetectsIndexLockFromFatalBlock()
    {
        const string stderr = """
            fatal: Unable to create '/Users/me/repo/.git/index.lock': File exists.

            Another git process seems to be running in this repository, e.g.
            an editor opened by this file. Please make sure all processes
            are terminated then try again.
            """;

        Assert.Equal("/Users/me/repo/.git/index.lock", GitLockFile.Detect(stderr));
    }

    [Fact]
    public void DetectsRefLock()
    {
        const string stderr = "error: Unable to create '/repo/.git/refs/heads/main.lock': File exists";
        Assert.Equal("/repo/.git/refs/heads/main.lock", GitLockFile.Detect(stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error: pathspec 'nope' did not match any file(s) known to git")]
    [InlineData("fatal: Unable to create '/repo/.git/index': File exists")]
    public void IgnoresNonLockFailures(string? error)
    {
        Assert.Null(GitLockFile.Detect(error));
    }

    [Fact]
    public void RemoveDeletesTheLockFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitbench-{Guid.NewGuid():N}.lock");
        File.WriteAllText(path, "");

        Assert.Null(GitLockFile.Remove(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveSucceedsWhenTheLockIsAlreadyGone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitbench-{Guid.NewGuid():N}.lock");
        Assert.Null(GitLockFile.Remove(path));
    }

    [Fact]
    public void RemoveRefusesPathsThatAreNotLockFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitbench-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "keep me");

        Assert.NotNull(GitLockFile.Remove(path));
        Assert.True(File.Exists(path));
        File.Delete(path);
    }
}
