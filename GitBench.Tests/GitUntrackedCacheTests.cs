using System.Diagnostics;
using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// §8: the opt-in core.untrackedCache setting. Drives the real GitService.ApplyUntrackedCache and the
// GitUntrackedCacheService over real throwaway repos. The apply is write-if-absent, respectful of a
// hand-set value, and never touches --global; the service applies only to primaries and only while
// the preference is on.
public sealed class GitUntrackedCacheTests : IDisposable
{
    private readonly string _root;

    public GitUntrackedCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-untracked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ---- GitService.ApplyUntrackedCache (real repo) ----

    [Fact]
    public void Sets_untracked_cache_when_absent_and_probe_passes()
    {
        var repo = InitRepo("solo");
        var git = new GitService(new RepoActivityTracker());

        var outcome = git.ApplyUntrackedCache(repo);

        Assert.IsType<GitOutcome.Success>(outcome);
        // On a filesystem where the probe passes (NTFS/ext4/APFS…) the key is written; where it
        // doesn't, declining leaves the key unset — either way is a success, never a false write.
        if (ProbeSupported(repo.Path))
            Assert.Equal("true", ReadLocal(repo.Path, "core.untrackedCache"));
        else
            Assert.Null(ReadLocal(repo.Path, "core.untrackedCache"));
    }

    [Fact]
    public void Second_apply_rewrites_nothing()
    {
        var repo = InitRepo("idempotent");
        var git = new GitService(new RepoActivityTracker());

        Assert.IsType<GitOutcome.Success>(git.ApplyUntrackedCache(repo));
        var after_first = ReadLocal(repo.Path, "core.untrackedCache");

        Assert.IsType<GitOutcome.Success>(git.ApplyUntrackedCache(repo));
        Assert.Equal(after_first, ReadLocal(repo.Path, "core.untrackedCache"));
    }

    [Fact]
    public void A_hand_set_false_is_left_false()
    {
        var repo = InitRepo("preset-false");
        Git(repo.Path, "config", "--local", "core.untrackedCache", "false");
        var git = new GitService(new RepoActivityTracker());

        Assert.IsType<GitOutcome.Success>(git.ApplyUntrackedCache(repo));

        Assert.Equal("false", ReadLocal(repo.Path, "core.untrackedCache"));
    }

    [Fact]
    public void A_hand_set_true_is_left_true()
    {
        var repo = InitRepo("preset-true");
        Git(repo.Path, "config", "--local", "core.untrackedCache", "true");
        var git = new GitService(new RepoActivityTracker());

        Assert.IsType<GitOutcome.Success>(git.ApplyUntrackedCache(repo));

        Assert.Equal("true", ReadLocal(repo.Path, "core.untrackedCache"));
    }

    [Fact]
    public void Global_config_is_never_written()
    {
        var repo = InitRepo("no-global");
        var git = new GitService(new RepoActivityTracker());
        var globalBefore = ReadGlobal("core.untrackedCache");

        git.ApplyUntrackedCache(repo);

        Assert.Equal(globalBefore, ReadGlobal("core.untrackedCache"));
    }

    // ---- GitUntrackedCacheService ----

    [Fact]
    public void Off_by_default_never_applies()
    {
        var registry = NewRegistry();
        var git = new CountingGitService(new GitService(new RepoActivityTracker())) { ThrowOnApplyUntrackedCache = true };
        var enabled = new State<bool>(false);
        using var svc = new GitUntrackedCacheService(registry, git, enabled);

        var repo = OpenRepo(registry, "off");
        svc.Start();
        // Nothing is scheduled while off, but give any stray task a moment to prove it never ran.
        Thread.Sleep(150);

        Assert.Equal(0, git.ApplyUntrackedCacheCalls);
        Assert.Null(ReadLocal(repo.Path, "core.untrackedCache"));
        registry.Dispose();
    }

    [Fact]
    public void Toggling_on_applies_to_every_registered_primary_but_not_child_rows()
    {
        var registry = NewRegistry();
        var git = new CountingGitService(new GitService(new RepoActivityTracker()));
        var enabled = new State<bool>(false);
        using var svc = new GitUntrackedCacheService(registry, git, enabled);

        var alpha = OpenRepo(registry, "alpha");
        var beta = OpenRepo(registry, "beta");
        // A worktree row shares its primary's config, so the service must skip it (the IsPrimary
        // filter is load-bearing — without it a linked config gets a redundant write).
        var worktree = new Repo(Guid.NewGuid(), Path.Combine(_root, "alpha-wt"), "alpha-wt", ParentRepoId: alpha.Id)
        {
            Kind = RepoKind.Worktree,
        };
        registry.Repos.Add(worktree);

        svc.Start();
        enabled.Value = true;

        WaitUntil(() => git.AppliedUntrackedCache.Count >= 2, "both primaries to be tuned");
        Thread.Sleep(200); // catch any stray extra apply before asserting the exact count

        Assert.Equal(2, git.ApplyUntrackedCacheCalls);
        var applied = git.AppliedUntrackedCache.Select(r => r.Id).ToHashSet();
        Assert.Contains(alpha.Id, applied);
        Assert.Contains(beta.Id, applied);
        Assert.DoesNotContain(worktree.Id, applied);
        registry.Dispose();
    }

    [Fact]
    public void A_repo_opened_while_enabled_gets_the_apply()
    {
        var registry = NewRegistry();
        var git = new CountingGitService(new GitService(new RepoActivityTracker()));
        var enabled = new State<bool>(true);
        using var svc = new GitUntrackedCacheService(registry, git, enabled);

        svc.Start();
        var fresh = OpenRepo(registry, "fresh");

        WaitUntil(() => git.AppliedUntrackedCache.Count >= 1, "the freshly-opened repo to be tuned");
        Thread.Sleep(200);

        Assert.Equal(1, git.ApplyUntrackedCacheCalls);
        Assert.Equal(fresh.Id, Assert.Single(git.AppliedUntrackedCache).Id);
        // The write completed (the applied list is recorded post-write); where the probe passes the
        // key is now set. The write path itself is covered deterministically by the GitService tests.
        if (ProbeSupported(fresh.Path))
            Assert.Equal("true", ReadLocal(fresh.Path, "core.untrackedCache"));
        registry.Dispose();
    }

    // ---- helpers ----

    private RepoRegistry NewRegistry()
    {
        var statePath = Path.Combine(_root, "repos-" + Guid.NewGuid().ToString("N") + ".json");
        return new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    private Repo OpenRepo(RepoRegistry registry, string name)
    {
        var repo = InitRepo(name);
        Assert.Equal(OpenRepoOutcome.Opened, registry.Open(repo.Path));
        return registry.Repos.Single(r => r.DisplayName == name);
    }

    private Repo InitRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(path, "a.txt"), "0");
        Git(path, "add", "a.txt");
        Git(path, "commit", "-qm", "base");
        return new Repo(Guid.NewGuid(), path, name);
    }

    private static bool ProbeSupported(string path)
        => RunGit(path, out _, "update-index", "--test-untracked-cache") == 0;

    private static string? ReadLocal(string path, string key)
    {
        var code = RunGit(path, out var stdout, "config", "--local", "--get", key);
        return code == 0 ? stdout.Trim() : null;
    }

    private static string? ReadGlobal(string key)
    {
        var code = RunGit(null, out var stdout, "config", "--global", "--get", key);
        return code == 0 ? stdout.Trim() : null;
    }

    private static void WaitUntil(Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (done()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static void Git(string cwd, params string[] args)
    {
        var code = RunGit(cwd, out var _, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}).");
    }

    private static int RunGit(string? cwd, out string stdout, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    public void Dispose()
    {
        try { ForceDelete(new DirectoryInfo(_root)); }
        catch { /* best effort: a leftover temp repo is harmless */ }
    }

    private static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}
