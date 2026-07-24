using System.Diagnostics;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The lock split is the whole fix for "a stage click during a fetch does nothing until the fetch
// ends": a network round-trip and an index write are different resources, so they must be different
// semaphores, and the remote one has to be shared by a repo family rather than by a working tree.
// These pin both halves — which paths contend, and which don't.
public sealed class GitRepoLocksTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-locks-");

    [Fact]
    public void The_two_resources_do_not_contend()
    {
        var locks = NoCommonDir();
        using var localState = locks.Acquire(GitResource.LocalState, "/repo");

        Assert.True(TryAcquire(locks, GitResource.Remote, "/repo"), "a fetch must not wait on a stage");
    }

    [Fact]
    public void The_same_resource_on_the_same_repo_contends()
    {
        var locks = NoCommonDir();
        using var held = locks.Acquire(GitResource.LocalState, "/repo");

        Assert.False(TryAcquire(locks, GitResource.LocalState, "/repo"));
    }

    [Fact]
    public void Different_repos_never_contend()
    {
        var locks = NoCommonDir();
        using var held = locks.Acquire(GitResource.LocalState, "/repo-a");

        Assert.True(TryAcquire(locks, GitResource.LocalState, "/repo-b"));
    }

    [Fact]
    public void Releasing_the_scope_frees_the_lock()
    {
        var locks = NoCommonDir();
        using (locks.Acquire(GitResource.LocalState, "/repo")) { }

        Assert.True(TryAcquire(locks, GitResource.LocalState, "/repo"));
    }

    // The remote lock is keyed by the git dir the family shares, so a primary and its linked
    // worktrees serialize their pushes against each other while keeping independent index locks.
    [Fact]
    public void A_shared_common_git_dir_shares_the_remote_lock_but_not_the_local_one()
    {
        var primary = Path.Combine(_dir.Path, "primary");
        var worktree = Path.Combine(_dir.Path, "wt");
        var locks = new GitRepoLocks(_ => Path.Combine(primary, ".git"));

        Assert.Equal(
            locks.KeyFor(GitResource.Remote, primary),
            locks.KeyFor(GitResource.Remote, worktree));
        Assert.NotEqual(
            locks.KeyFor(GitResource.LocalState, primary),
            locks.KeyFor(GitResource.LocalState, worktree));
    }

    [Fact]
    public void A_relative_common_git_dir_resolves_against_the_working_tree()
    {
        var repo = Path.Combine(_dir.Path, "repo");
        var relative = new GitRepoLocks(_ => ".git");
        var absolute = new GitRepoLocks(_ => Path.Combine(repo, ".git"));

        Assert.Equal(
            absolute.KeyFor(GitResource.Remote, repo),
            relative.KeyFor(GitResource.Remote, repo));
    }

    // "Couldn't tell" must degrade to today's behaviour — a per-working-tree remote lock — rather
    // than throwing out of a lock acquisition and failing the op.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unresolvable_common_git_dir_falls_back_to_the_working_tree(string? resolved)
    {
        var locks = new GitRepoLocks(_ => resolved);

        Assert.Equal(
            locks.KeyFor(GitResource.LocalState, "/repo"),
            locks.KeyFor(GitResource.Remote, "/repo"));
    }

    [Fact]
    public void A_throwing_resolver_falls_back_to_the_working_tree()
    {
        var locks = new GitRepoLocks(_ => throw new IOException("git is gone"));

        Assert.Equal(
            locks.KeyFor(GitResource.LocalState, "/repo"),
            locks.KeyFor(GitResource.Remote, "/repo"));
    }

    [Fact]
    public void The_common_git_dir_is_resolved_once_per_working_tree()
    {
        var calls = 0;
        var locks = new GitRepoLocks(p => { calls++; return Path.Combine(p, ".git"); });

        locks.KeyFor(GitResource.Remote, "/repo");
        locks.KeyFor(GitResource.Remote, "/repo");
        using (locks.Acquire(GitResource.Remote, "/repo")) { }

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Trailing_separators_are_the_same_repo()
    {
        var locks = NoCommonDir();
        using var held = locks.Acquire(GitResource.LocalState, Path.Combine(_dir.Path, "repo"));

        Assert.False(TryAcquire(locks, GitResource.LocalState, Path.Combine(_dir.Path, "repo") + Path.DirectorySeparatorChar));
    }

    // End to end against real git: a linked worktree's `.git` is a file pointing into the primary's
    // `.git/worktrees/<name>`, and only `--git-common-dir` collapses that back to the shared root.
    [Fact]
    public void Real_git_resolves_a_linked_worktree_onto_the_primary_family()
    {
        var primary = Path.Combine(_dir.Path, "primary");
        Directory.CreateDirectory(primary);
        Git(primary, "init", "-q", "-b", "main");
        Git(primary, "config", "user.name", "Test");
        Git(primary, "config", "user.email", "test@example.com");
        Git(primary, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(primary, "a.txt"), "0");
        Git(primary, "add", "a.txt");
        Git(primary, "commit", "-qm", "base");

        var worktree = Path.Combine(_dir.Path, "wt");
        Git(primary, "worktree", "add", "-q", worktree, "-b", "side");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a linked worktree's .git is a file");

        var locks = new GitRepoLocks(RealCommonGitDir);

        Assert.Equal(
            locks.KeyFor(GitResource.Remote, primary),
            locks.KeyFor(GitResource.Remote, worktree));
        Assert.NotEqual(
            locks.KeyFor(GitResource.LocalState, primary),
            locks.KeyFor(GitResource.LocalState, worktree));
    }

    // ---- helpers ----

    private static GitRepoLocks NoCommonDir() => new(_ => null);

    // A lock acquisition blocks, so "is it free?" has to be asked from another thread with a
    // deadline. Generous enough that a loaded CI box doesn't read as contention.
    private static bool TryAcquire(GitRepoLocks locks, GitResource resource, string path)
    {
        var task = Task.Run(() =>
        {
            using var _ = locks.Acquire(resource, path);
        });
        return task.Wait(TimeSpan.FromSeconds(2));
    }

    private static string? RealCommonGitDir(string repoPath)
    {
        var output = Run(repoPath, "rev-parse", "--git-common-dir");
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static void Git(string cwd, params string[] args) => Run(cwd, args);

    private static string Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose() => _dir.Dispose();
}
