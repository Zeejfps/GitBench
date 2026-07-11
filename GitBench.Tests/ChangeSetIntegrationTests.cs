using System.Diagnostics;
using GitBench.Features.Branches;
using GitBench.Features.ChangeSets;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// Phase 7 — verification. Real-git integration tests over the whole change-set stack, built on the
// ReviewStackTests/SyncedBranchIndexTests fixture pattern: throwaway sibling repos created with the git
// CLI, driven through the real GitService / GitReviewStackSource / ChangeSetOperations cores (no fakes,
// no mocks). Covers the matrix the plan's Phase 7 calls for:
//   - index detection across add / delete / rename of branches
//   - batch create / checkout / delete over real repos
//   - batch commit trailer (Change-Set: <name> stamped per member; unstaged members skipped)
//   - cross-repo range aggregation with per-member base resolution (main vs. master defaults)
//   - partial-failure reporting (one member fails for a real git reason, the rest still run)
//   - the drift/health cases the manual pass would eyeball (dirty tree, unpushed commit, no upstream,
//     branch deleted in one repo) — exercised headlessly through the status + aggregation layer.
public sealed class ChangeSetIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git;
    private readonly List<Repo> _repos = new();

    public ChangeSetIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-cs-integ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new RepoActivityTracker());
    }

    // ---- index detection across add / delete / rename ----

    [Fact]
    public void Detection_AddingASharedBranchInASecondRepo_FormsANewSet()
    {
        // feature/x starts in service-a only (a decoy — not a set), then is added to service-b. The
        // correlator must go from "no set" to a two-member set once the second repo carries the name.
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main");

        Assert.Empty(CorrelateLive(a, b));

        CreateBranch(b, "feature/x", "main");

        var set = Assert.Single(CorrelateLive(a, b));
        Assert.Equal("feature/x", set.BranchName);
        Assert.Equal(new[] { a.Id, b.Id }, set.RepoIds);
    }

    [Fact]
    public void Detection_DeletingTheBranchInOneMember_ShrinksTheSetBelowTwo_SoItDisappears()
    {
        // feature/x in both repos is a set of two; delete it in service-b and only service-a carries it,
        // which is a decoy, not a set — the set is gone, not a one-member set.
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main", "feature/x");

        var set = Assert.Single(CorrelateLive(a, b));
        Assert.Equal(new[] { a.Id, b.Id }, set.RepoIds);

        Git(b.Path, "branch", "-D", "feature/x");

        Assert.Empty(CorrelateLive(a, b));
    }

    [Fact]
    public void Detection_DeletingInOneOfThree_ShrinksTheSetToTheRemainingTwo()
    {
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main", "feature/x");
        var c = Fixture("service-c", "main", "feature/x");

        Assert.Equal(3, Assert.Single(CorrelateLive(a, b, c)).RepoIds.Count);

        Git(b.Path, "branch", "-D", "feature/x");

        var set = Assert.Single(CorrelateLive(a, b, c));
        // The remaining two are still a set, in group order (b is dropped, not shuffled).
        Assert.Equal(new[] { a.Id, c.Id }, set.RepoIds);
    }

    [Fact]
    public void Detection_RenamingTheBranchInOneMember_BreaksTheOldSet_AndCanFormANewOne()
    {
        // feature/x across a+b+c; rename it to feature/y in c. The old name now correlates a+b only,
        // and feature/y is a decoy (c alone). Rename it in b too and feature/y becomes its own set.
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main", "feature/x");
        var c = Fixture("service-c", "main", "feature/x");

        Git(c.Path, "branch", "-m", "feature/x", "feature/y");

        var afterRename = CorrelateLive(a, b, c);
        var x = Assert.Single(afterRename, s => s.BranchName == "feature/x");
        Assert.Equal(new[] { a.Id, b.Id }, x.RepoIds);
        Assert.DoesNotContain(afterRename, s => s.BranchName == "feature/y"); // c alone: decoy

        Git(b.Path, "branch", "-m", "feature/x", "feature/y");

        // Now feature/y is the two-member set {b, c}, and feature/x survives in a alone — a decoy, no set.
        var afterSecondRename = CorrelateLive(a, b, c);
        var y = Assert.Single(afterSecondRename, s => s.BranchName == "feature/y");
        Assert.Equal(new[] { b.Id, c.Id }, y.RepoIds);
        Assert.DoesNotContain(afterSecondRename, s => s.BranchName == "feature/x");
    }

    // ---- batch create / checkout / delete over real repos ----

    [Fact]
    public void BatchCreate_CreatesTheBranchAndChecksItOutInEveryMember_FromItsOwnStartPoint()
    {
        // service-a/b default to main, service-c to master — the reason per-repo start points exist.
        var a = Fixture("service-a", "main");
        var b = Fixture("service-b", "main");
        var c = Fixture("service-c", "master");
        var startById = new Dictionary<Guid, string>
        {
            [a.Id] = "main",
            [b.Id] = "main",
            [c.Id] = "master",
        };

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c },
            r => _git.CreateBranch(r, "feature/new", ChangeSetOperations.ResolveStartPoint(startById, r.Id), checkout: true));

        Assert.True(result.AllSucceeded);
        // Every member now HAS the branch and is CHECKED OUT on it (checkout: true).
        foreach (var repo in new[] { a, b, c })
        {
            Assert.Contains("feature/new", LocalBranches(repo));
            Assert.Equal("feature/new", CurrentBranch(repo));
        }
    }

    [Fact]
    public void BatchCheckout_SwitchesEveryMemberToTheSharedBranch()
    {
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main", "feature/x");
        var c = Fixture("service-c", "main", "feature/x");
        // All three start on their default branch (Fixture switches back after making the branch).
        Assert.All(new[] { a, b, c }, r => Assert.Equal("main", CurrentBranch(r)));

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c }, r => _git.CheckoutLocalBranch(r, "feature/x"));

        Assert.True(result.AllSucceeded);
        Assert.All(new[] { a, b, c }, r => Assert.Equal("feature/x", CurrentBranch(r)));
    }

    [Fact]
    public void BatchDelete_RemovesTheBranchFromEveryMember()
    {
        var a = Fixture("service-a", "main", "feature/x");
        var b = Fixture("service-b", "main", "feature/x");

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b }, r => _git.DeleteBranch(r, "feature/x", force: true));

        Assert.True(result.AllSucceeded);
        Assert.DoesNotContain("feature/x", LocalBranches(a));
        Assert.DoesNotContain("feature/x", LocalBranches(b));
    }

    // ---- partial-failure reporting ----

    [Fact]
    public void BatchDelete_CheckedOutMemberFails_OthersStillDeleted_ReportedPerRepo()
    {
        // feature/x in all three (pointing at main's tip, so a non-force delete is allowed where it's not
        // checked out); service-b has it CHECKED OUT, so git refuses to delete it there while a and c (on
        // main) delete cleanly. The result records b's failure beside the successes, nothing is rolled
        // back — the honest partial-success outcome (Locked decision #5).
        var a = Fixture("service-a", "main");
        var b = Fixture("service-b", "main");
        var c = Fixture("service-c", "main");
        foreach (var r in new[] { a, b, c }) CreateBranch(r, "feature/x", "main");
        Git(b.Path, "switch", "feature/x"); // b now sits on the branch we try to delete

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c }, r => _git.DeleteBranch(r, "feature/x", force: false));

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.IsType<GitOutcome.Success>(result.Results[0].Outcome);           // a deleted
        var failed = Assert.IsType<GitOutcome.Failed>(result.Results[1].Outcome); // b refused
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));                 // a legible reason
        Assert.IsType<GitOutcome.Success>(result.Results[2].Outcome);           // c deleted
        // Reality matches the report: a and c lost the branch, b (checked out) kept it.
        Assert.DoesNotContain("feature/x", LocalBranches(a));
        Assert.Contains("feature/x", LocalBranches(b));
        Assert.DoesNotContain("feature/x", LocalBranches(c));
    }

    [Fact]
    public void BatchCreate_NameCollisionInOneMember_ReportsThatRepo_StillCreatesTheOthers()
    {
        // service-b already has feature/dup; creating it again there fails, while a and c get it fresh.
        var a = Fixture("service-a", "main");
        var b = Fixture("service-b", "main", "feature/dup");
        var c = Fixture("service-c", "main");
        var startById = new Dictionary<Guid, string>(); // all blank → HEAD

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c },
            r => _git.CreateBranch(r, "feature/dup", ChangeSetOperations.ResolveStartPoint(startById, r.Id), checkout: true));

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.IsType<GitOutcome.Failed>(result.Results[1].Outcome);
        Assert.Contains("feature/dup", LocalBranches(a));
        Assert.Contains("feature/dup", LocalBranches(c));
    }

    // ---- batch commit trailer ----

    [Fact]
    public void BatchCommit_StampsTheChangeSetTrailerPerMember_AndSkipsMembersWithNothingStaged()
    {
        // Three members on feature/cross-repo. Stage an edit in a and c; leave b clean. The batch commit
        // must stamp "Change-Set: feature/cross-repo" into a's and c's real commit messages and skip b
        // entirely (b's HEAD must not move).
        var a = Fixture("service-a", "main", "feature/cross-repo");
        var b = Fixture("service-b", "main", "feature/cross-repo");
        var c = Fixture("service-c", "main", "feature/cross-repo");
        foreach (var r in new[] { a, b, c }) Git(r.Path, "switch", "feature/cross-repo");

        StageEdit(a, "a.txt", "edit-a");
        StageEdit(c, "c.txt", "edit-c");
        var bHeadBefore = Head(b);

        var full = ChangeSetOperations.StampTrailer("Coordinate the shared field", "feature/cross-repo");
        var result = ChangeSetOperations.CommitOverMembers(
            new[] { a, b, c },
            hasStaged: r => _git.GetLocalChanges(r) is Fetched<GitBench.Features.LocalChanges.LocalChangesSnapshot>.Ok ok && ok.Value.Staged.Count > 0,
            commit: r => _git.Commit(r, full, amend: false));

        Assert.True(result.AllSucceeded);
        // Only a and c committed (b had nothing staged), in member order.
        Assert.Equal(new[] { a.Id, c.Id }, result.Results.Select(x => x.RepoId));
        Assert.Contains("Change-Set: feature/cross-repo", LastCommitMessage(a));
        Assert.Contains("Change-Set: feature/cross-repo", LastCommitMessage(c));
        Assert.Contains("Coordinate the shared field", LastCommitMessage(a));
        // The trailer sits after exactly one blank line at the end of the message body.
        Assert.EndsWith("\n\nChange-Set: feature/cross-repo", LastCommitMessage(a).TrimEnd('\n'));
        // b was skipped: its HEAD did not move.
        Assert.Equal(bHeadBefore, Head(b));
    }

    // ---- cross-repo range aggregation with per-member base resolution ----

    [Fact]
    public void Aggregation_ResolvesEachMembersOwnBase_MainVsMaster_ThroughTheRealStackSource()
    {
        // service-a's default is main, service-c's is master; both carry feature/cross-repo with two
        // increments off their own default. The real GitReviewStackSource auto-resolves each member's
        // base to its OWN default branch — the per-member base resolution the plan calls out — and the
        // aggregator folds both into one ordered list of Ok loads.
        var a = FeatureRepo("service-a", "main", "feature/cross-repo", ("f1.txt", "1"), ("f2.txt", "2"));
        var c = FeatureRepo("service-c", "master", "feature/cross-repo", ("g1.txt", "1"), ("g2.txt", "2"));

        var registry = NewRegistry();
        registry.Open(a.Path);
        registry.Open(c.Path);
        var ra = registry.Repos.First(r => r.Path == a.Path);
        var rc = registry.Repos.First(r => r.Path == c.Path);
        using var loc = new LocalizationService(new State<Locale>(Locale.En));
        var source = new GitReviewStackSource(registry, _git, loc);

        var members = new[]
        {
            new ReviewSession(ra.Id, "feature/cross-repo", "feature/cross-repo", null, null),
            new ReviewSession(rc.Id, "feature/cross-repo", "feature/cross-repo", null, null),
        };
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (ra.Id, "service-a"), (rc.Id, "service-c") });

        var loads = ChangeSetAggregator.LoadAll(source, members, keys, cap: 200);

        Assert.Equal(new[] { ra.Id, rc.Id }, loads.Select(l => l.RepoId));
        var okA = Assert.IsType<ChangeSetMemberLoad.Ok>(loads[0]);
        var okC = Assert.IsType<ChangeSetMemberLoad.Ok>(loads[1]);
        // Each member's base is its OWN default branch — main for a, master for c (per-member resolution).
        Assert.Equal("main", okA.Stack.BaseRef);
        Assert.Equal(ReviewBaseKind.DefaultBranch, okA.Stack.BaseKind);
        Assert.Equal("master", okC.Stack.BaseRef);
        Assert.Equal(ReviewBaseKind.DefaultBranch, okC.Stack.BaseKind);
        // Two increments each (the two feature commits), oldest→newest.
        Assert.Equal(2, okA.Stack.Increments.Count);
        Assert.Equal(2, okC.Stack.Increments.Count);
    }

    [Fact]
    public void Aggregation_MemberWhoseBranchWasDeleted_FoldsIntoFailed_OthersStillOk()
    {
        // service-a keeps feature/cross-repo; service-c had it but it was deleted (the "branch missing in
        // one repo" drift case). c's stack fails to resolve → an inline Failed member, a still Ok — the
        // window never dies (Phase 3 failure isolation, which the health strip reads as Unavailable).
        var a = FeatureRepo("service-a", "main", "feature/cross-repo", ("f1.txt", "1"));
        var c = FeatureRepo("service-c", "master", "feature/cross-repo", ("g1.txt", "1"));
        Git(c.Path, "switch", "master");
        Git(c.Path, "branch", "-D", "feature/cross-repo"); // deleted in this member

        var registry = NewRegistry();
        registry.Open(a.Path);
        registry.Open(c.Path);
        var ra = registry.Repos.First(r => r.Path == a.Path);
        var rc = registry.Repos.First(r => r.Path == c.Path);
        using var loc = new LocalizationService(new State<Locale>(Locale.En));
        var source = new GitReviewStackSource(registry, _git, loc);

        var members = new[]
        {
            new ReviewSession(ra.Id, "feature/cross-repo", "feature/cross-repo", null, null),
            new ReviewSession(rc.Id, "feature/cross-repo", "feature/cross-repo", null, null),
        };
        var keys = RepoQualifiedPaths.BuildKeys(new[] { (ra.Id, "service-a"), (rc.Id, "service-c") });

        var loads = ChangeSetAggregator.LoadAll(source, members, keys, cap: 200);

        Assert.IsType<ChangeSetMemberLoad.Ok>(loads[0]);
        var failed = Assert.IsType<ChangeSetMemberLoad.Failed>(loads[1]);
        Assert.Equal("service-c", failed.RepoKey);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));

        // And that failed load is exactly what flips a member's health to Unavailable (the red state).
        var health = ChangeSetMemberHealth.From("service-c", loadFailed: true, aheadOfBase: 0, RepoStatus.Unknown);
        Assert.True(health.Unavailable);
        Assert.True(health.NeedsAttention);
    }

    // ---- drift / health cases through the real status layer ----

    [Fact]
    public void Health_DirtyWorkingTree_ReadsAsAttention_ThroughTheRealStatusProbe()
    {
        // A member with an uncommitted edit on the set branch (the fixture's service-c dirty-tree case).
        var c = Fixture("service-c", "main", "feature/cross-repo");
        Git(c.Path, "switch", "feature/cross-repo");
        File.WriteAllText(Path.Combine(c.Path, "wip.txt"), "uncommitted");

        var status = ToRepoStatus(_git.GetStatusSummary(c));
        var health = ChangeSetMemberHealth.From("service-c", loadFailed: false, aheadOfBase: 0, status);

        Assert.True(status.IsDirty);
        Assert.True(health.Dirty);
        Assert.True(health.NeedsAttention);
    }

    [Fact]
    public void Health_UnpushedCommit_ReadsAsAttention_AheadOfBaseAloneDoesNot()
    {
        // Publish main to a bare remote, then commit locally so the branch sits 1 ahead of its upstream
        // (the fixture's service-a unpushed-commit case). Unpushed is attention; the base..head range
        // count (aheadOfBase) is the reviewed change and is NOT drift.
        var a = Fixture("service-a", "main");
        var remote = Path.Combine(_root, "service-a-remote.git");
        Git(_root, "init", "--bare", "-b", "main", remote);
        Git(a.Path, "remote", "add", "origin", remote);
        Git(a.Path, "push", "-u", "origin", "main");
        Commit(a.Path, "later.txt", "1", "unpushed work"); // now 1 ahead of origin/main

        var status = ToRepoStatus(_git.GetStatusSummary(a));
        Assert.True(status.HasUpstream);
        Assert.Equal(1, status.Ahead);

        var health = ChangeSetMemberHealth.From("service-a", loadFailed: false, aheadOfBase: 5, status);
        Assert.Equal(1, health.Unpushed);
        Assert.Equal(5, health.AheadOfBase); // surfaced…
        Assert.True(health.NeedsAttention);  // …because of the unpushed commit, not the range count

        // Prove ahead-of-base alone is not drift: a clean, fully-pushed member with a big range is quiet.
        var pushedClean = ToRepoStatus(_git.GetStatusSummary(a)) with { Ahead = 0 };
        var quiet = ChangeSetMemberHealth.From("x", loadFailed: false, aheadOfBase: 9, pushedClean);
        Assert.False(quiet.NeedsAttention);
        Assert.True(quiet.IsQuiet);
    }

    [Fact]
    public void Health_FreshBranchWithNoUpstream_IsInformationalNotAttention()
    {
        // A just-started set branch that was never pushed has no upstream. That's informational (a distinct
        // tooltip line) but must not light up the strip — Phase 6 keeps a not-yet-pushed set reading "in sync".
        var a = Fixture("service-a", "main", "feature/fresh");
        Git(a.Path, "switch", "feature/fresh");

        var status = ToRepoStatus(_git.GetStatusSummary(a));
        var health = ChangeSetMemberHealth.From("service-a", loadFailed: false, aheadOfBase: 1, status);

        Assert.False(status.HasUpstream);
        Assert.True(health.NoUpstream);
        Assert.False(health.NeedsAttention);
        Assert.False(health.IsQuiet); // worth a tooltip line, just not an attention flag
    }

    // ---------------------------------------------------------------------------------------------
    // fixtures + helpers
    // ---------------------------------------------------------------------------------------------

    // Builds a repo whose default branch is `defaultBranch` plus the given extra feature branches (each
    // created off the default with its own commit), then switches back to the default and returns the Repo.
    private Repo Fixture(string name, string defaultBranch, params string[] extraBranches)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", defaultBranch);
        ConfigureIdentity(dir);
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

    // Builds a repo left CHECKED OUT on `branch`, which carries `commits` off the default branch — the
    // shape the range aggregation test needs (a feature branch with real increments over its own default).
    private Repo FeatureRepo(string name, string defaultBranch, string branch, params (string File, string Content)[] commits)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", defaultBranch);
        ConfigureIdentity(dir);
        Commit(dir, "base.txt", "0", "base");
        Git(dir, "switch", "-c", branch);
        foreach (var (file, content) in commits)
            Commit(dir, file, content, "work: " + file);
        var repo = new Repo(Guid.NewGuid(), dir, name);
        _repos.Add(repo);
        return repo;
    }

    private RepoRegistry NewRegistry()
    {
        var statePath = Path.Combine(_root, $"state-{Guid.NewGuid():N}.json");
        var state = RepoStateStore.Load(statePath);
        return new RepoRegistry(state, statePath);
    }

    private void CreateBranch(Repo repo, string name, string startPoint) =>
        Assert.IsType<GitOutcome.Success>(_git.CreateBranch(repo, name, startPoint, checkout: false));

    // Writes + stages an edit (leaves it in the index for a batch commit).
    private void StageEdit(Repo repo, string file, string content)
    {
        File.WriteAllText(Path.Combine(repo.Path, file), content);
        Git(repo.Path, "add", file);
    }

    private static RepoStatus ToRepoStatus(GitStatusSummary? s)
    {
        Assert.NotNull(s);
        return new RepoStatus(s!.Branch, s.IsDetached, s.HasUpstream, s.Ahead, s.Behind, s.IsDirty,
            IsBusy: false, HasUnseenError: false);
    }

    // Reads each repo through the real GitService (branches + default) and runs the pure correlator over
    // them as one group, in the order given — the live detection path the index wraps.
    private IReadOnlyList<SyncedBranch> CorrelateLive(params Repo[] group)
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

    private IReadOnlyList<string> LocalBranches(Repo repo) =>
        Assert.IsType<Fetched<BranchListing>.Ok>(_git.GetBranches(repo)).Value.LocalBranches.Select(b => b.Name).ToList();

    private string CurrentBranch(Repo repo) => Git(repo.Path, "rev-parse", "--abbrev-ref", "HEAD").Trim();

    private string Head(Repo repo) => Git(repo.Path, "rev-parse", "HEAD").Trim();

    private string LastCommitMessage(Repo repo) => Git(repo.Path, "log", "-1", "--format=%B");

    private static void ConfigureIdentity(string dir)
    {
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "commit.gpgsign", "false");
    }

    private static void Commit(string dir, string file, string content, string message)
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
