using System.Diagnostics;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Load builds the history view's snapshot out of `git for-each-ref`, `git stash list` and a
// `git log --topo-order --stdin` walk seeded with every displayed ref. These drive the real
// GitService against a throwaway repo wired to a throwaway bare "origin", pinning the parts that
// only the ref scan can get wrong: which tips seed the walk, which badges land on which commit,
// and how a local branch resolves against its upstream.
public sealed class CommitGraphLoadTests : IDisposable
{
    private readonly string _work;
    private readonly string _origin;
    private readonly GitService _git;
    private readonly Repo _repo;

    public CommitGraphLoadTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbench-graph-" + Guid.NewGuid().ToString("N"));
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
    public void Commits_are_returned_newest_first_with_author_and_parents()
    {
        var first = Commit("a.txt", "0", "first");
        var second = Commit("a.txt", "1", "second");

        var nodes = Load().Commits;

        Assert.Equal(new[] { second, first }, nodes.Select(n => n.Sha).ToArray());
        Assert.Equal("second", nodes[0].Summary);
        Assert.Equal("Test", nodes[0].Author);
        Assert.Equal(new[] { first }, nodes[0].ParentShas.ToArray());
        Assert.Empty(nodes[1].ParentShas);
    }

    [Fact]
    public void A_subject_containing_the_field_separator_does_not_split_the_record()
    {
        Commit("a.txt", "0", "fix: a\x1Fb and c");

        Assert.Equal("fix: a\x1Fb and c", Load().Commits.Single().Summary);
    }

    [Fact]
    public void The_checked_out_branch_is_marked_current()
    {
        Commit("a.txt", "0", "base");

        var badge = BadgesOf(Load(), "main").Single(b => b.Kind == RefKind.LocalBranch);
        Assert.True(badge.IsCurrent);
    }

    [Fact]
    public void A_branch_level_with_its_upstream_reads_as_in_sync_and_absorbs_the_remote_badge()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");

        var badges = BadgesOf(Load(), "main");
        Assert.Equal(RefSyncState.InSync, badges.Single(b => b.Kind == RefKind.LocalBranch).Sync);
        Assert.DoesNotContain(badges, b => b.Kind == RefKind.RemoteBranch);
    }

    [Fact]
    public void A_branch_ahead_of_its_upstream_reads_as_diverged_and_keeps_both_badges()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        var ahead = Commit("a.txt", "1", "ahead");

        var snapshot = Load();
        Assert.Equal(RefSyncState.Diverged, Badges(snapshot, ahead).Single(b => b.Kind == RefKind.LocalBranch).Sync);
        Assert.Contains(AllBadges(snapshot), b => b.Kind == RefKind.RemoteBranch && b.Name == "origin/main");
    }

    [Fact]
    public void A_branch_with_no_upstream_reads_as_untracked()
    {
        Commit("a.txt", "0", "base");

        Assert.Equal(RefSyncState.Untracked, BadgesOf(Load(), "main").Single(b => b.Kind == RefKind.LocalBranch).Sync);
    }

    [Fact]
    public void An_upstream_whose_remote_ref_was_deleted_falls_back_to_untracked()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "doomed");
        Commit("b.txt", "0", "on doomed");
        Git("push", "-u", "origin", "doomed");
        Git("push", "origin", "--delete", "doomed");
        Git("fetch", "--prune");

        var badge = BadgesOf(Load(), "doomed").Single(b => b.Kind == RefKind.LocalBranch);
        Assert.Equal(RefSyncState.Untracked, badge.Sync);
    }

    [Fact]
    public void Both_tag_kinds_peel_to_their_commit()
    {
        var first = Commit("a.txt", "0", "tagged");
        Git("tag", "light");
        Git("tag", "-a", "ann", "-m", "annotated");
        Commit("a.txt", "1", "later");

        var names = Badges(Load(), first).Where(b => b.Kind == RefKind.Tag).Select(b => b.Name).OrderBy(n => n);
        Assert.Equal(new[] { "ann", "light" }, names.ToArray());
    }

    [Fact]
    public void A_tag_that_does_not_name_a_commit_is_ignored()
    {
        Commit("a.txt", "0", "base");
        var blob = Git("hash-object", "-w", "--stdin", "-t", "blob").Trim();
        Git("tag", "blobtag", blob);

        Assert.DoesNotContain(AllBadges(Load()), b => b.Kind == RefKind.Tag);
    }

    [Fact]
    public void A_tagged_commit_stays_in_the_walk_when_no_branch_reaches_it()
    {
        Commit("a.txt", "0", "base");
        Git("checkout", "-b", "side");
        var orphan = Commit("b.txt", "0", "only reachable by tag");
        Git("tag", "keeper");
        Git("checkout", "main");
        Git("branch", "-D", "side");

        var node = Load().Commits.Single(n => n.Sha == orphan);
        Assert.Contains(node.Refs, b => b.Kind == RefKind.Tag && b.Name == "keeper");
    }

    [Fact]
    public void Stash_commits_are_walked_and_badged()
    {
        Commit("a.txt", "0", "base");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "dirty");
        Git("stash");

        var stashSha = Git("rev-parse", "refs/stash").Trim();
        var node = Load().Commits.Single(n => n.Sha == stashSha);
        Assert.Contains(node.Refs, b => b.Kind == RefKind.Stash);
    }

    [Fact]
    public void A_detached_head_gets_its_own_badge_and_seeds_the_walk()
    {
        var first = Commit("a.txt", "0", "first");
        Commit("a.txt", "1", "second");
        Git("checkout", "--detach", first);

        var snapshot = Load();
        Assert.Null(snapshot.HeadBranchName);
        Assert.Contains(Badges(snapshot, first), b => b.Kind == RefKind.Head && b.Name == "HEAD");
    }

    [Fact]
    public void The_remote_only_flag_marks_commits_no_local_ref_reaches()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        var remoteOnly = Commit("a.txt", "1", "pushed but not kept locally");
        Git("push", "origin", "main");
        Git("reset", "--hard", "HEAD~1");
        Git("fetch");

        var snapshot = Load();
        Assert.True(snapshot.Commits.Single(n => n.Sha == remoteOnly).RemoteOnly);
        Assert.False(snapshot.Commits.Last().RemoteOnly);
    }

    [Fact]
    public void The_cap_truncates_and_reports_it()
    {
        Commit("a.txt", "0", "first");
        Commit("a.txt", "1", "second");
        Commit("a.txt", "2", "third");

        var snapshot = Load(cap: 2);
        Assert.Equal(2, snapshot.Commits.Count);
        Assert.True(snapshot.Truncated);
        Assert.False(Load(cap: 3).Truncated);
    }

    [Fact]
    public void A_repo_with_no_commits_loads_empty_rather_than_failing()
    {
        var snapshot = Load();
        Assert.Empty(snapshot.Commits);
        Assert.False(snapshot.Truncated);
    }

    // ---- helpers ----

    private CommitSnapshot Load(int cap = 100) =>
        Assert.IsType<Fetched<CommitSnapshot>.Ok>(_git.Load(_repo, cap)).Value;

    private static IReadOnlyList<RefBadge> Badges(CommitSnapshot snapshot, string sha) =>
        snapshot.Commits.Single(n => n.Sha == sha).Refs;

    private static IReadOnlyList<RefBadge> BadgesOf(CommitSnapshot snapshot, string branch) =>
        snapshot.Commits.Single(n => n.Refs.Any(b => b.Name == branch)).Refs;

    private static IEnumerable<RefBadge> AllBadges(CommitSnapshot snapshot) =>
        snapshot.Commits.SelectMany(n => n.Refs);

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(_work, file), content);
        Git("add", file);
        Git("commit", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] args) => RunGit(_work, args);

    private static string RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        _git.Dispose();
        try { ForceDelete(new DirectoryInfo(Path.GetDirectoryName(_work)!)); }
        catch { /* best effort: a leftover temp repo is harmless */ }
    }

    // git marks loose objects read-only, which trips Directory.Delete on Windows; clear attributes
    // depth-first before removing.
    private static void ForceDelete(DirectoryInfo dir)
    {
        if (!dir.Exists) return;
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}
