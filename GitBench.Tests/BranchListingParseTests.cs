using System.Diagnostics;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// GetBranches parses `git for-each-ref` into the local-branch union: the checked-out branch becomes
// LocalBranchEntry.Head (upstream existence only — no counts, those belong to IRepoStatusStore),
// every other local branch becomes LocalBranchEntry.Other carrying a LocalUpstream case, and remote
// refs become RemoteBranchEntry (no upstream concept at all). Each test drives the real GitService
// against a throwaway repo wired to a throwaway bare "origin".
public sealed class BranchListingParseTests : IDisposable
{
    private readonly string _work;
    private readonly string _origin;
    private readonly GitService _git;
    private readonly Repo _repo;

    public BranchListingParseTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "gitbench-branches-" + Guid.NewGuid().ToString("N"));
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

    // ---- the checked-out branch ----

    [Fact]
    public void Checked_out_branch_parses_as_head_with_a_tracked_upstream()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");

        var head = Assert.IsType<LocalBranchEntry.Head>(Local(Listing(), "main"));
        Assert.Equal(HeadUpstreamState.Tracked, head.Upstream);
    }

    [Fact]
    public void Checked_out_branch_without_an_upstream_parses_as_head_with_no_upstream()
    {
        Commit("a.txt", "0", "base");

        var head = Assert.IsType<LocalBranchEntry.Head>(Local(Listing(), "main"));
        Assert.Equal(HeadUpstreamState.None, head.Upstream);
    }

    [Fact]
    public void Checked_out_branch_whose_upstream_was_deleted_parses_as_head_gone()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "doomed");
        Git("push", "-u", "origin", "doomed");
        Git("push", "origin", "--delete", "doomed");
        Git("fetch", "--prune");

        var head = Assert.IsType<LocalBranchEntry.Head>(Local(Listing(), "doomed"));
        Assert.Equal(HeadUpstreamState.Gone, head.Upstream);
    }

    [Fact]
    public void Exactly_one_local_entry_is_the_head()
    {
        Commit("a.txt", "0", "base");
        Git("branch", "one");
        Git("branch", "two");

        var locals = Listing().LocalBranches;

        Assert.Equal(3, locals.Count);
        var head = Assert.Single(locals.OfType<LocalBranchEntry.Head>());
        Assert.Equal("main", head.Name);
    }

    [Fact]
    public void A_detached_head_produces_no_head_entry()
    {
        Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "second");
        Git("checkout", "--detach", "HEAD~1");

        // Nothing is checked out by name, so every listed local branch is an Other. HEAD's state is
        // the status store's to report — the listing must not invent a head row for it.
        Assert.Empty(Listing().LocalBranches.OfType<LocalBranchEntry.Head>());
    }

    // ---- non-checked-out branches ----

    [Fact]
    public void Branch_ahead_of_its_upstream_carries_both_counts()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "feature");
        Commit("f.txt", "1", "feature work");
        Git("push", "-u", "origin", "feature");
        Commit("f.txt", "2", "more feature work");
        Git("checkout", "main");

        var tracked = Tracked(Local(Listing(), "feature"));

        Assert.Equal("origin", tracked.Remote);
        Assert.Equal("feature", tracked.Branch);
        // "ahead 1" alone used to leave BehindBy null; the pair is now always whole.
        Assert.Equal(new BranchSync(1, 0), tracked.Sync);
    }

    [Fact]
    public void Branch_behind_its_upstream_carries_both_counts()
    {
        var first = Commit("a.txt", "0", "base");
        Commit("a.txt", "1", "second");
        Git("push", "-u", "origin", "main");
        Git("branch", "lagging", first);
        Git("branch", "--set-upstream-to=origin/main", "lagging");

        var tracked = Tracked(Local(Listing(), "lagging"));

        Assert.Equal("origin", tracked.Remote);
        Assert.Equal("main", tracked.Branch);
        Assert.Equal(new BranchSync(0, 1), tracked.Sync);
    }

    [Fact]
    public void Branch_in_sync_with_its_upstream_carries_a_zero_pair()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "feature");
        Git("push", "-u", "origin", "feature");
        Git("checkout", "main");

        Assert.Equal(new BranchSync(0, 0), Tracked(Local(Listing(), "feature")).Sync);
    }

    [Fact]
    public void Branch_with_no_upstream_parses_as_none()
    {
        Commit("a.txt", "0", "base");
        Git("branch", "solo");

        var other = Assert.IsType<LocalBranchEntry.Other>(Local(Listing(), "solo"));
        Assert.IsType<LocalUpstream.None>(other.Upstream);
    }

    [Fact]
    public void Branch_whose_upstream_was_deleted_parses_as_gone()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "doomed");
        Git("push", "-u", "origin", "doomed");
        Git("checkout", "main");
        Git("push", "origin", "--delete", "doomed");
        Git("fetch", "--prune");

        var other = Assert.IsType<LocalBranchEntry.Other>(Local(Listing(), "doomed"));
        Assert.IsType<LocalUpstream.Gone>(other.Upstream);
    }

    // ---- remote refs ----

    [Fact]
    public void Remote_refs_parse_into_their_remote_group()
    {
        Commit("a.txt", "0", "base");
        Git("push", "-u", "origin", "main");
        Git("checkout", "-b", "feature/login");
        Git("push", "-u", "origin", "feature/login");
        Git("checkout", "main");

        var group = Assert.Single(Listing().Remotes);

        Assert.Equal("origin", group.Name);
        Assert.Equal(["feature/login", "main"], group.Branches.Select(b => b.Name));
    }

    [Fact]
    public void A_remote_with_no_branches_still_produces_an_empty_group()
    {
        Commit("a.txt", "0", "base");

        var group = Assert.Single(Listing().Remotes);

        Assert.Equal("origin", group.Name);
        Assert.Empty(group.Branches);
    }

    // ---- helpers ----

    private BranchListing Listing() =>
        Assert.IsType<Fetched<BranchListing>.Ok>(_git.GetBranches(_repo)).Value;

    private static LocalBranchEntry Local(BranchListing listing, string name) =>
        listing.LocalBranches.Single(b => b.Name == name);

    private static LocalUpstream.Tracked Tracked(LocalBranchEntry entry) =>
        Assert.IsType<LocalUpstream.Tracked>(Assert.IsType<LocalBranchEntry.Other>(entry).Upstream);

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

    public void Dispose()
    {
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
