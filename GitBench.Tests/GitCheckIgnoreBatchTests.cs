using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

public sealed class GitCheckIgnoreBatchTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;

    public GitCheckIgnoreBatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-checkignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");

        Git("init");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "build/\n*.log\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
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
    public void AnEmptyBatchIsAnsweredWithoutAskingGit()
    {
        Assert.Empty(_git.IsPathIgnored(_repo, []));
    }
}
