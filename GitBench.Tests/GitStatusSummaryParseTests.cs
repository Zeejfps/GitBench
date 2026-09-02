using System.Diagnostics;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

// §2: one `git status --porcelain=v2 --branch` yields both the file lists and the branch / ahead /
// behind / dirty summary. These drive the real GitService against a throwaway repo wired to a
// throwaway bare "origin" and assert the parsed GitStatusSummary for each repo state — and, the
// item's whole point, that GetLocalChanges(repo).Summary equals GetStatusSummary(repo) every time.
//
// GetSyncSummary is a third read of the same facts from refs alone, so it is held to the same bar:
// AssertBothAgree checks it against the status probe's sync half in every state below. It is only
// safe to answer a fetch with the cheap read if the two cannot disagree about where HEAD is.
public sealed class GitStatusSummaryParseTests : IDisposable
{
    private readonly string _work;
    private readonly string _origin;
    private readonly GitService _git;
    private readonly Repo _repo;

    public GitStatusSummaryParseTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbench-summary-" + Guid.NewGuid().ToString("N"));
        _work = Path.Combine(root, "work");
        _origin = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_origin);

        RunGit(_origin, "init", "--bare", "-b", "main");
        Git("init", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("remote", "add", "origin", _origin.Replace('\\', '/'));

        _git = new GitService(new RepoActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _work, "test");
    }

    [Fact]
    public void Tracked_and_diverged_reports_branch_upstream_and_both_counts()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        AdvanceOriginByOneCommit();
        Commit("c.txt", "1", "local work");
        Git("fetch");

        var s = Probe();
        Assert.Equal("main", s.Branch);
        Assert.False(s.IsDetached);
        Assert.True(s.HasUpstream);
        Assert.Equal(1, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.False(s.IsDirty);
        AssertBothAgree();
    }

    [Fact]
    public void No_upstream_configured_reports_branch_with_no_upstream_and_zero_counts()
    {
        Commit("a.txt", "0", "base");

        var s = Probe();
        Assert.Equal("main", s.Branch);
        Assert.False(s.HasUpstream);
        Assert.Equal(0, s.Ahead);
        Assert.Equal(0, s.Behind);
        AssertBothAgree();
    }

    [Fact]
    public void Detached_head_reports_detached_with_no_branch_or_upstream()
    {
        Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "second");
        Git("checkout", "--detach", "HEAD~1");

        var s = Probe();
        Assert.True(s.IsDetached);
        Assert.Null(s.Branch);
        Assert.False(s.HasUpstream);
        AssertBothAgree();
    }

    [Fact]
    public void Unborn_branch_reports_its_name_with_no_upstream()
    {
        // No commit at all: `# branch.oid (initial)` / `# branch.head main`, nothing else.
        var s = Probe();
        Assert.Equal("main", s.Branch);
        Assert.False(s.IsDetached);
        Assert.False(s.HasUpstream);
        Assert.False(s.IsDirty);
        AssertBothAgree();
    }

    [Fact]
    public void A_clean_tree_is_not_dirty()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");

        Assert.False(Probe().IsDirty);
        AssertBothAgree();
    }

    [Fact]
    public void An_untracked_file_alone_is_dirty()
    {
        Commit("a.txt", "0", "base");
        WriteFile("loose.txt", "x");

        Assert.True(Probe().IsDirty);
        AssertBothAgree();
    }

    [Fact]
    public void A_staged_change_alone_is_dirty()
    {
        Commit("a.txt", "0", "base");
        WriteFile("s.txt", "new");
        Git("add", "s.txt");

        Assert.True(Probe().IsDirty);
        AssertBothAgree();
    }

    [Fact]
    public void An_unmerged_path_is_dirty()
    {
        Commit("a.txt", "0", "base");
        Git("checkout", "-b", "other");
        WriteFile("a.txt", "other side");
        Git("add", "a.txt");
        Git("commit", "-m", "other");
        Git("checkout", "main");
        WriteFile("a.txt", "main side");
        Git("add", "a.txt");
        Git("commit", "-m", "main");
        GitAllowFail("merge", "other"); // leaves an unmerged path

        Assert.True(Probe().IsDirty);
        AssertBothAgree();
    }

    // The file lists must be byte-for-byte unaffected by --branch: no header may leak into either
    // list, and the rename record's trailing NUL-terminated origPath must still be consumed.
    [Fact]
    public void The_file_lists_are_unchanged_by_the_branch_headers()
    {
        Commit("a.txt", "0", "base a");
        Commit("b.txt", "0", "base b");

        Git("mv", "b.txt", "b-renamed.txt");       // staged rename
        WriteFile("s.txt", "new");
        Git("add", "s.txt");                        // staged add
        WriteFile("a.txt", "modified");             // unstaged modify
        WriteFile("u1.txt", "x");                   // untracked file
        Directory.CreateDirectory(Path.Combine(_work, "untrackeddir", "nested"));
        WriteFile(Path.Combine("untrackeddir", "nested", "u2.txt"), "y"); // untracked tree

        var snap = FileList();

        Assert.Equal(
            new[] { ("b-renamed.txt", FileChangeStatus.Renamed), ("s.txt", FileChangeStatus.Added) },
            snap.Staged.Select(f => (f.Path, f.Status)));
        Assert.Equal("b.txt", Assert.Single(snap.Staged, f => f.Status == FileChangeStatus.Renamed).OldPath);

        Assert.Equal(
            new[]
            {
                ("a.txt", FileChangeStatus.Modified),
                ("u1.txt", FileChangeStatus.Added),
                ("untrackeddir/nested/u2.txt", FileChangeStatus.Added),
            },
            snap.Unstaged.Select(f => (f.Path, f.Status)));
    }

    // ---- helpers ----

    private GitStatusSummary Probe()
    {
        var s = _git.GetStatusSummary(_repo);
        Assert.NotNull(s);
        return s!;
    }

    private LocalChangesSnapshot FileList() =>
        Assert.IsType<Fetched<LocalChangesSnapshot>.Ok>(_git.GetLocalChanges(_repo)).Value;

    private GitSyncSummary Sync()
    {
        var s = _git.GetSyncSummary(_repo);
        Assert.NotNull(s);
        return s!;
    }

    // The whole point of §2: both reads describe one observation, so their summaries must be equal.
    // The refs-only read is the third account of the same repo state and must match the sync half.
    private void AssertBothAgree()
    {
        var probe = Probe();
        Assert.Equal(probe, FileList().Summary);
        Assert.Equal(
            new GitSyncSummary(probe.Branch, probe.IsDetached, probe.HasUpstream, probe.Ahead, probe.Behind),
            Sync());
    }

    // An upstream configured but no longer present: porcelain-v2 emits a branch.upstream header
    // with no branch.ab, and for-each-ref says "[gone]". Both mean "tracking, nothing to report".
    [Fact]
    public void A_gone_upstream_still_reports_tracking_with_zero_counts()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("update-ref", "-d", "refs/remotes/origin/main");

        var s = Sync();
        Assert.True(s.HasUpstream);
        Assert.Equal(0, s.Ahead);
        Assert.Equal(0, s.Behind);
        AssertBothAgree();
    }

    // The one number the whole fetch path exists to move: fetching while the remote is ahead must
    // show up in the refs-only read, with no working-tree walk involved.
    [Fact]
    public void A_fetch_shows_up_in_the_refs_only_read()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        AdvanceOriginByOneCommit();

        Assert.Equal(0, Sync().Behind);

        Git("fetch");

        Assert.Equal(1, Sync().Behind);
        AssertBothAgree();
    }

    private void AdvanceOriginByOneCommit()
    {
        var parent = Path.GetDirectoryName(_work)!;
        var clone = Path.Combine(parent, "clone");
        RunGit(parent, "clone", _origin.Replace('\\', '/'), clone);
        RunGit(clone, "config", "user.name", "Test");
        RunGit(clone, "config", "user.email", "test@example.com");
        RunGit(clone, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(clone, "remote-change.txt"), "r");
        RunGit(clone, "add", "remote-change.txt");
        RunGit(clone, "commit", "-m", "remote work");
        RunGit(clone, "push", "origin", "main");
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_work, name), content);

    private void Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(_work, file), content);
        Git("add", file);
        Git("commit", "-m", message);
    }

    private void Git(params string[] args) => RunGit(_work, args);

    private void GitAllowFail(params string[] args)
    {
        var psi = StartInfo(_work, args);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
    }

    private static string RunGit(string cwd, params string[] args)
    {
        using var proc = Process.Start(StartInfo(cwd, args))!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    private static ProcessStartInfo StartInfo(string cwd, string[] args)
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
        return psi;
    }

    public void Dispose()
    {
        DirectoryTree.Delete(Path.GetDirectoryName(_work)!);
    }
}
