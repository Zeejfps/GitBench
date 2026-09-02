using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Features.Worktrees;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

// Adding a worktree against a real repo with a submodule. `git worktree add` populates the
// superproject only, so a worktree of a repo with submodules arrives with empty submodule
// directories unless the add is followed by `git submodule update --init` inside it.
public sealed class WorktreeAddTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    // A submodule reached over a filesystem path is refused since CVE-2022-39253, and the clone
    // `submodule update` spawns runs outside the superproject — so its local config never reaches
    // it. GIT_CONFIG_* does, and it inherits into every git this process starts, including the
    // ones GitService runs. Set once and left set: the whole suite tolerates it, and clearing it
    // could strand a concurrent git on a half-removed GIT_CONFIG_COUNT.
    private static readonly bool FileTransportAllowed = AllowFileTransport();

    private static bool AllowFileTransport()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        return true;
    }

    private readonly string _root;
    private readonly string _repoPath;
    private readonly GitService _git;
    private readonly Repo _repo;

    public WorktreeAddTests()
    {
        Assert.True(FileTransportAllowed);
        _root = Path.Combine(Path.GetTempPath(), "gitbench-wtadd-" + Guid.NewGuid().ToString("N"));
        _repoPath = Path.Combine(_root, "primary");

        var subPath = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subPath);
        Git(subPath, "init");
        Git(subPath, "config", "user.email", "test@example.com");
        Git(subPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(subPath, "sub.txt"), "submodule");
        Git(subPath, "add", ".");
        Git(subPath, "commit", "-m", "sub");

        Directory.CreateDirectory(_repoPath);
        Git(_repoPath, "init");
        Git(_repoPath, "config", "user.email", "test@example.com");
        Git(_repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repoPath, "file.txt"), "hello");
        Git(_repoPath, "add", ".");
        Git(_repoPath, "commit", "-m", "init");
        Git(_repoPath, "submodule", "add", "../sub", "sub");
        Git(_repoPath, "commit", "-m", "add submodule");

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _repoPath, "primary");
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_root);
    }

    [Fact]
    public void InitializesSubmodulesInTheNewWorktree()
    {
        var path = Path.Combine(_root, "with-submodules");

        var outcome = _git.AddWorktree(_repo, Request(path, "with-submodules", initSubmodules: true));

        Assert.Equal(WorktreeAddOutcome.Ok, outcome);
        Assert.True(File.Exists(Path.Combine(path, "sub", "sub.txt")));
    }

    [Fact]
    public void LeavesSubmodulesUninitializedWhenNotAsked()
    {
        var path = Path.Combine(_root, "bare-submodules");

        var outcome = _git.AddWorktree(_repo, Request(path, "bare-submodules", initSubmodules: false));

        Assert.Equal(WorktreeAddOutcome.Ok, outcome);
        Assert.True(Directory.Exists(Path.Combine(path, "sub")));
        Assert.False(File.Exists(Path.Combine(path, "sub", "sub.txt")));
    }

    [Fact]
    public void ReportsAFailedAddInsteadOfRunningTheSubmoduleStep()
    {
        var outcome = _git.AddWorktree(_repo, Request(_repoPath, "occupied", initSubmodules: true));

        Assert.IsType<WorktreeAddOutcome.Failed>(outcome);
    }

    private static WorktreeAddRequest Request(string path, string branch, bool initSubmodules)
        => new(
            Path: path,
            StartPoint: "HEAD",
            NewBranchName: branch,
            Force: false,
            InitSubmodules: initSubmodules,
            RecurseSubmodules: initSubmodules);

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
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("protocol.file.allow=always");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }
}
