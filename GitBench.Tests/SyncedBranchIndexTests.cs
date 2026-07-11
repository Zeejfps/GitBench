using System.Diagnostics;
using GitBench.Features.Branches;
using GitBench.Features.ChangeSets;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Phase 1 detection: same-named local branches across a group's primaries form a change set, with
// each repo's own default branch excluded. Each test builds throwaway sibling repos on disk via the
// git CLI (the ReviewStackTests fixture pattern), reads each repo's branches + default through the
// real GitService, and drives the pure SyncedBranchCorrelator — the grouping/diffing logic the index
// wraps. Mirrors the 70-change-set scenario in scripts/make-test-repos.sh.
public sealed class SyncedBranchIndexTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git;
    private readonly List<Repo> _repos = new();

    public SyncedBranchIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-changeset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new RepoActivityTracker());
    }

    [Fact]
    public void Correlate_SameBranchInThreeRepos_IsOneSetOfThree()
    {
        var a = Fixture("service-a", "main", "feature/cross-repo");
        var b = Fixture("service-b", "main", "feature/cross-repo");
        var c = Fixture("service-c", "main", "feature/cross-repo");

        var sets = Correlate(a, b, c);

        var set = Assert.Single(sets);
        Assert.Equal("feature/cross-repo", set.BranchName);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, set.RepoIds);
    }

    [Fact]
    public void Correlate_BranchInTwoOfThree_IsOneSetOfTwoInGroupOrder()
    {
        var a = Fixture("service-a", "main");
        var b = Fixture("service-b", "main", "bugfix/shared-logging");
        var c = Fixture("service-c", "main", "bugfix/shared-logging");

        var sets = Correlate(a, b, c);

        var set = Assert.Single(sets);
        Assert.Equal("bugfix/shared-logging", set.BranchName);
        // Only the two members that carry it, in group-membership order (a is skipped, not first).
        Assert.Equal(new[] { b.Id, c.Id }, set.RepoIds);
    }

    [Fact]
    public void Correlate_BranchInOneRepoOnly_IsNotASet()
    {
        var a = Fixture("service-a", "main", "feature/only-in-a");
        var b = Fixture("service-b", "main");

        Assert.Empty(Correlate(a, b));
    }

    [Fact]
    public void Correlate_DefaultBranchesEverywhere_AreNotASet_EvenWhenNamesMatch()
    {
        // main in two repos, main in a third — every member's default, so never a set.
        var a = Fixture("service-a", "main");
        var b = Fixture("service-b", "main");
        var c = Fixture("service-c", "main");

        Assert.Empty(Correlate(a, b, c));
    }

    [Fact]
    public void Correlate_ExclusionIsPerRepoDefault_NotTheLiteralNameMain()
    {
        // Two repos default to main; the third's default is master. No default is shared as a set,
        // and a real feature branch that happens to be named "master" in the main-default repos would
        // still correlate — proving exclusion keys off each repo's own default, not the name "main".
        var a = Fixture("service-a", "main", "feature/cross-repo");
        var b = Fixture("service-b", "main", "feature/cross-repo");
        var c = Fixture("service-c", "master", "feature/cross-repo"); // default is master here

        var sets = Correlate(a, b, c);

        var set = Assert.Single(sets);
        Assert.Equal("feature/cross-repo", set.BranchName);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, set.RepoIds);
    }

    [Fact]
    public void Correlate_FullFixtureShape_YieldsBothSetsAndIgnoresDecoys()
    {
        // Mirrors 70-change-set: feature/cross-repo across all three, bugfix/shared-logging in b+c,
        // feature/only-in-a as a decoy, defaults main/main/master.
        var a = Fixture("service-a", "main", "feature/cross-repo", "feature/only-in-a");
        var b = Fixture("service-b", "main", "feature/cross-repo", "bugfix/shared-logging");
        var c = Fixture("service-c", "master", "feature/cross-repo", "bugfix/shared-logging");

        var sets = Correlate(a, b, c);

        Assert.Equal(2, sets.Count);
        var cross = Assert.Single(sets, s => s.BranchName == "feature/cross-repo");
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, cross.RepoIds);
        var logging = Assert.Single(sets, s => s.BranchName == "bugfix/shared-logging");
        Assert.Equal(new[] { b.Id, c.Id }, logging.RepoIds);
        Assert.DoesNotContain(sets, s => s.BranchName == "feature/only-in-a");
        Assert.DoesNotContain(sets, s => s.BranchName is "main" or "master");
    }

    [Fact]
    public void GetDefaultBranchName_ReflectsEachReposOwnDefault()
    {
        var a = Fixture("service-a", "main");
        var c = Fixture("service-c", "master");

        Assert.Equal("main", _git.GetDefaultBranchName(a));
        Assert.Equal("master", _git.GetDefaultBranchName(c));
    }

    // Builds a repo whose default branch is `defaultBranch` plus the given extra feature branches,
    // then returns its Repo handle. The first commit lands on the default branch; each extra branch
    // is created off it with its own commit.
    private Repo Fixture(string name, string defaultBranch, params string[] extraBranches)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", defaultBranch);
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "commit.gpgsign", "false");
        Commit(dir, "base.txt", "0", "base");
        foreach (var branch in extraBranches)
        {
            Git(dir, "switch", "-c", branch);
            Commit(dir, branch.Replace('/', '_') + ".txt", "1", "work on " + branch);
            Git(dir, "switch", defaultBranch);
        }
        var repo = new Repo(Guid.NewGuid(), dir, name);
        _repos.Add(repo);
        return repo;
    }

    // Reads each repo through the real GitService (branches + default) and runs the pure correlator
    // over them as one group, in the order given.
    private IReadOnlyList<SyncedBranch> Correlate(params Repo[] group)
    {
        var byRepo = new Dictionary<Guid, RepoBranchSnapshot>();
        foreach (var repo in group)
        {
            var listing = Assert.IsType<Fetched<BranchListing>.Ok>(_git.GetBranches(repo)).Value;
            byRepo[repo.Id] = new RepoBranchSnapshot(
                _git.GetDefaultBranchName(repo),
                listing.LocalBranches.Select(b => b.Name).ToList());
        }
        return SyncedBranchCorrelator.Correlate(group.Select(r => r.Id).ToList(), byRepo);
    }

    private void Commit(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, "add", file);
        Git(dir, "commit", "-m", message);
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
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
