using System.Diagnostics;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

// A freshly `git init`ed repo has an unborn HEAD: files can be staged, but nothing resolves
// HEAD until the first commit lands. Working-tree operations must still work there, or the
// user is trapped — they can stage and then never take it back.
public sealed class GitUnbornHeadTests : IDisposable
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

    public GitUnbornHeadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-unborn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_root);
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

    private LocalChangesSnapshot Snapshot()
    {
        var fetched = _git.GetLocalChanges(_repo);
        return Assert.IsType<Fetched<LocalChangesSnapshot>.Ok>(fetched).Value;
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_root, name), content);

    [Fact]
    public void Unstage_ReturnsFilesToUntracked_BeforeTheFirstCommit()
    {
        Write("a.txt", "one\n");
        Write("b.txt", "two\n");
        Assert.True(_git.Stage(_repo, new[] { "a.txt", "b.txt" }) is GitOutcome.Success);
        Assert.Equal(2, Snapshot().Staged.Count);

        var unstaged = _git.Unstage(_repo, new[] { "a.txt", "b.txt" });

        Assert.True(unstaged is GitOutcome.Success, unstaged.FailureMessage);
        var after = Snapshot();
        Assert.Empty(after.Staged);
        Assert.Equal(new[] { "a.txt", "b.txt" }, after.Unstaged.Select(f => f.Path).OrderBy(p => p));
    }

    [Fact]
    public void Unstage_LeavesUnnamedPathsStaged_BeforeTheFirstCommit()
    {
        Write("keep.txt", "keep\n");
        Write("drop.txt", "drop\n");
        Assert.True(_git.Stage(_repo, new[] { "keep.txt", "drop.txt" }) is GitOutcome.Success);

        Assert.True(_git.Unstage(_repo, new[] { "drop.txt" }) is GitOutcome.Success);

        var after = Snapshot();
        Assert.Equal(new[] { "keep.txt" }, after.Staged.Select(f => f.Path));
        Assert.Equal(new[] { "drop.txt" }, after.Unstaged.Select(f => f.Path));
    }

    [Fact]
    public void Unstage_KeepsWorkingTreeContent_BeforeTheFirstCommit()
    {
        Write("a.txt", "precious\n");
        Assert.True(_git.Stage(_repo, new[] { "a.txt" }) is GitOutcome.Success);

        Assert.True(_git.Unstage(_repo, new[] { "a.txt" }) is GitOutcome.Success);

        Assert.Equal("precious\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }
}
