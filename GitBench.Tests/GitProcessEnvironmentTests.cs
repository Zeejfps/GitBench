using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;

using Xunit;

namespace GitBench.Tests;

public sealed class GitProcessEnvironmentTests : IDisposable
{
    private readonly string _root;
    private readonly GitProcessRunner _runner = new(new NullActivityTracker());

    public GitProcessEnvironmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitprocenv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _runner.Run(_root, new[] { "init", "-q", "." });
    }

    [Fact]
    public void RunsWithoutAShellWrapper()
    {
        var result = _runner.Run(_root, new[] { "rev-parse", "--is-inside-work-tree" });
        Assert.True(result.Ok, result.BlockError("git rev-parse"));
        Assert.Equal("true", result.Stdout.Trim());
    }

    [Fact]
    public void ChildProcessesSeeTheLoginShellPath()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var result = _runner.Run(_root, new[] { "-c", "alias.showpath=!printf %s \"$PATH\"", "showpath" });
        Assert.True(result.Ok, result.BlockError("git showpath"));

        var seen = result.Stdout.Split(':');
        Assert.Contains(Path.GetDirectoryName(ResolvedGitDir()), seen);
    }

    // A git that never started is a result, not an exception: every read above the runner relies
    // on that to have nothing left to catch.
    [Fact]
    public void AMissingWorkingDirectoryIsAResultNotAnException()
    {
        var gone = Path.Combine(_root, "does-not-exist");

        var result = _runner.Run(gone, new[] { "status" });

        Assert.False(result.Started);
        Assert.False(result.Ok);
        Assert.StartsWith("Failed to start git", result.FirstLineError("git status"));
        Assert.StartsWith("Failed to start git", result.BlockError("git status"));
    }

    [Fact]
    public void RepoScopingVariablesNeverReachGit()
    {
        var psi = _runner.BuildLongRunningPsi(_root, new[] { "cat-file", "--batch" });
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "PWD" })
            Assert.False(psi.Environment.ContainsKey(key), key);
    }

    private string ResolvedGitDir()
    {
        var result = _runner.Run(_root, new[] { "-c", "alias.whichgit=!command -v git", "whichgit" });
        return result.Stdout.Trim();
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_root);
    }
}
