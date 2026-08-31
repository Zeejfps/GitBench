using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Removing a worktree against a real repo. The case that matters is the one git can't finish on
// its own: a working tree containing a junction (pnpm's node_modules), where `git worktree remove`
// deregisters the worktree, gives up on the delete, and exits 1 — which used to surface as a
// failure even though the worktree was gone.
public sealed class WorktreeRemoveTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _root;
    private readonly string _repoPath;
    private readonly GitService _git;
    private readonly Repo _repo;

    public WorktreeRemoveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-wt-" + Guid.NewGuid().ToString("N"));
        _repoPath = Path.Combine(_root, "primary");
        Directory.CreateDirectory(_repoPath);

        Git(_repoPath, "init");
        Git(_repoPath, "config", "user.email", "test@example.com");
        Git(_repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repoPath, "file.txt"), "hello");
        Git(_repoPath, "add", ".");
        Git(_repoPath, "commit", "-m", "init");

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _repoPath, "primary");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"mklink /J failed: {stderr}");
    }

    private string AddWorktree(string name)
    {
        var path = Path.Combine(_root, name);
        Git(_repoPath, "worktree", "add", "-b", name, path, "HEAD");
        return path;
    }

    // git prints a worktree path with every symlink on it already followed, so the two sides only
    // compare equal through RealPath — on macOS the temp directory these tests run in is reached
    // through one.
    private bool IsRegistered(string path)
        => _git.ListWorktrees(_repo).Any(w =>
            string.Equals(RealPath.Of(w.Path), RealPath.Of(path), StringComparison.OrdinalIgnoreCase));

    // A junction whose target is gone. git follows junctions while deleting, so it empties one
    // package's directory and turns every junction pointing there into this — then aborts the whole
    // walk at the first one, having already deregistered the worktree:
    //   error: failed to delete '<worktree>': Directory not empty
    [Fact]
    public void RemovesAWorktreeGitCannotFinishDeletingItself()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_root, "link-target");
        Directory.CreateDirectory(target);

        var worktree = AddWorktree("with-junction");
        Directory.CreateDirectory(Path.Combine(worktree, "node_modules"));
        Junction(Path.Combine(worktree, "node_modules", "dep"), target);
        Directory.Delete(target);

        var outcome = _git.RemoveWorktree(_repo, worktree, force: true);

        Assert.IsType<WorktreeRemoveOutcome.Removed>(outcome);
        Assert.False(Directory.Exists(worktree));
        Assert.False(IsRegistered(worktree));
    }

    [Fact]
    public void RemovesACleanWorktree()
    {
        var worktree = AddWorktree("clean");

        var outcome = _git.RemoveWorktree(_repo, worktree, force: false);

        Assert.IsType<WorktreeRemoveOutcome.Removed>(outcome);
        Assert.False(Directory.Exists(worktree));
        Assert.False(IsRegistered(worktree));
    }

    [Fact]
    public void FailsAndKeepsTheTreeWhenGitRefuses()
    {
        var worktree = AddWorktree("dirty");
        File.WriteAllText(Path.Combine(worktree, "untracked.txt"), "unsaved work");

        var outcome = _git.RemoveWorktree(_repo, worktree, force: false);

        var failed = Assert.IsType<WorktreeRemoveOutcome.Failed>(outcome);
        Assert.NotEmpty(failed.Message);
        Assert.True(File.Exists(Path.Combine(worktree, "untracked.txt")));
        Assert.True(IsRegistered(worktree));
    }

    // The cleanup only ever applies to a worktree git has just deregistered; a path git never had
    // registered must come back as a plain failure with its contents untouched.
    [Fact]
    public void NeverDeletesAPathThatIsNotARegisteredWorktree()
    {
        var stranger = Path.Combine(_root, "not-a-worktree");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "precious.txt"), "do not delete");

        var outcome = _git.RemoveWorktree(_repo, stranger, force: true);

        Assert.IsType<WorktreeRemoveOutcome.Failed>(outcome);
        Assert.True(File.Exists(Path.Combine(stranger, "precious.txt")));
    }
}
