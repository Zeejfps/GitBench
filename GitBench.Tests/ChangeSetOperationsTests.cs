using GitBench.Features.Branches;
using GitBench.Features.ChangeSets;
using GitBench.Features.Commits;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Operations;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Phase 2 coordinator: ChangeSetOperations.RunOverMembers is the pure, synchronous core the batch
// actions (checkout/push/pull/fetch/delete in all) run — one IGitService call per member, per-repo
// GitOutcomes collected with no rollup and no rollback (Locked decision #5). These tests drive it
// over a fake IGitService and assert the essential guarantees: outcomes are recorded per repo in
// member order, a failed member never blocks the rest, and a thrown call folds into that member's
// Failed outcome.
public sealed class ChangeSetOperationsTests
{
    private static Repo Repo(string name) => new(Guid.NewGuid(), $"/tmp/{name}", name);

    [Fact]
    public void RunOverMembers_AllSucceed_RecordsOkPerRepoInMemberOrder()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");

        var result = ChangeSetOperations.RunOverMembers(new[] { a, b, c }, r => git.Push(r));

        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, result.Results.Select(x => x.RepoId));
        Assert.All(result.Results, x => Assert.IsType<GitOutcome.Success>(x.Outcome));
        // Every member was called — the loop touches all of them.
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, git.Calls.Select(c => c.RepoId));
    }

    [Fact]
    public void RunOverMembers_OneFails_OthersStillRunAndAreReportedPerRepo()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        git.Results[b.Id] = new GitOutcome.Failed("no remote configured");

        var result = ChangeSetOperations.RunOverMembers(new[] { a, b, c }, r => git.Push(r));

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        // The failed member is recorded beside the successes — nothing is rolled back.
        Assert.IsType<GitOutcome.Success>(result.Results[0].Outcome);
        var failed = Assert.IsType<GitOutcome.Failed>(result.Results[1].Outcome);
        Assert.Equal("no remote configured", failed.Message);
        Assert.IsType<GitOutcome.Success>(result.Results[2].Outcome);
        // The failure in b did not stop c from running.
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, git.Calls.Select(c => c.RepoId));
    }

    [Fact]
    public void RunOverMembers_ThrownCall_FoldsIntoThatMembersFailed_LoopContinues()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        git.ThrowFor.Add(a.Id);

        var result = ChangeSetOperations.RunOverMembers(new[] { a, b, c }, r => git.DeleteBranch(r, "feature/x", force: false));

        var failed = Assert.IsType<GitOutcome.Failed>(result.Results[0].Outcome);
        Assert.Equal("boom", failed.Message);
        // b and c still ran and succeeded despite a throwing first.
        Assert.IsType<GitOutcome.Success>(result.Results[1].Outcome);
        Assert.IsType<GitOutcome.Success>(result.Results[2].Outcome);
        Assert.Equal(2, result.SuccessCount);
        Assert.Contains(git.Calls, x => x.RepoId == c.Id);
    }

    [Fact]
    public void RunOverMembers_AllFail_IsPartialWithZeroSuccesses_NotAnException()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        git.Results[a.Id] = new GitOutcome.Failed("locked");
        git.Results[b.Id] = new GitOutcome.Failed("locked");

        var result = ChangeSetOperations.RunOverMembers(new[] { a, b }, r => git.CheckoutLocalBranch(r, "feature/x"));

        Assert.False(result.AllSucceeded);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public void RunOverMembers_EmptyMembers_IsEmptySuccess()
    {
        var result = ChangeSetOperations.RunOverMembers(Array.Empty<Repo>(), _ => GitOutcome.Ok);

        Assert.Empty(result.Results);
        Assert.True(result.AllSucceeded); // vacuously — nothing failed
        Assert.Equal(0, result.SuccessCount);
    }

    // Phase 4: "Start change set" is a CreateBranch loop. These drive the same op CreateInAll builds
    // (RunOverMembers + ResolveStartPoint over the fake) so the per-repo start-point mapping and the
    // name-collision isolation are pinned without standing up the fire-and-forget coordinator.

    [Fact]
    public void Create_MapsPerRepoStartPoints_AndCreatesEveryMember()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        // Each member starts from its own default branch (main/main/master) — the reason per-repo
        // start points exist at all.
        var startById = new Dictionary<Guid, string>
        {
            [a.Id] = "main",
            [b.Id] = "main",
            [c.Id] = "master",
        };

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c },
            r => git.CreateBranch(r, "feature/y", ChangeSetOperations.ResolveStartPoint(startById, r.Id), checkout: true));

        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, git.CreateCalls.Select(x => x.RepoId));
        Assert.All(git.CreateCalls, x => Assert.Equal("feature/y", x.Name));
        Assert.All(git.CreateCalls, x => Assert.True(x.Checkout)); // checkout: true — all members switch
        Assert.Equal("main", git.CreateCalls.Single(x => x.RepoId == a.Id).StartPoint);
        Assert.Equal("master", git.CreateCalls.Single(x => x.RepoId == c.Id).StartPoint);
    }

    [Fact]
    public void Create_NameCollisionInOneRepo_ReportsThatRepo_StillCreatesOthers()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        git.Results[b.Id] = new GitOutcome.Failed("a branch named 'feature/y' already exists");
        var startById = new Dictionary<Guid, string>(); // all blank → HEAD

        var result = ChangeSetOperations.RunOverMembers(
            new[] { a, b, c },
            r => git.CreateBranch(r, "feature/y", ChangeSetOperations.ResolveStartPoint(startById, r.Id), checkout: true));

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.IsType<GitOutcome.Success>(result.Results[0].Outcome);
        var failed = Assert.IsType<GitOutcome.Failed>(result.Results[1].Outcome);
        Assert.Contains("already exists", failed.Message);
        Assert.IsType<GitOutcome.Success>(result.Results[2].Outcome);
        // c was still created despite b's collision — no rollback, a failed member never blocks the rest.
        Assert.Contains(git.CreateCalls, x => x.RepoId == c.Id);
        // A blank start-point map falls back to HEAD for every member.
        Assert.All(git.CreateCalls, x => Assert.Equal("HEAD", x.StartPoint));
    }

    [Fact]
    public void ResolveStartPoint_TrimsExplicit_FallsBackToHeadWhenBlankOrMissing()
    {
        var withValue = Guid.NewGuid();
        var blank = Guid.NewGuid();
        var map = new Dictionary<Guid, string>
        {
            [withValue] = "  develop  ",
            [blank] = "   ",
        };

        Assert.Equal("develop", ChangeSetOperations.ResolveStartPoint(map, withValue));
        Assert.Equal("HEAD", ChangeSetOperations.ResolveStartPoint(map, blank));
        Assert.Equal("HEAD", ChangeSetOperations.ResolveStartPoint(map, Guid.NewGuid()));
    }

    // Phase 5.4: batch commit. CommitOverMembers is the pure core CommitInAll runs — commit only the
    // members with staged changes (5.5: no auto-staging), stamp the Change-Set trailer (Locked #6), and
    // fold a member's failure into its own outcome without blocking or rolling back the rest (Locked #5).

    [Fact]
    public void StampTrailer_AppendsChangeSetTrailer_AfterABlankLine()
    {
        Assert.Equal(
            "Fix the thing\n\nChange-Set: feature/x",
            ChangeSetOperations.StampTrailer("Fix the thing", "feature/x"));
        // Trailing whitespace on the body is trimmed so the trailer always sits after exactly one blank line.
        Assert.Equal(
            "subject\n\nbody\n\nChange-Set: feature/x",
            ChangeSetOperations.StampTrailer("subject\n\nbody\n\n", "feature/x"));
    }

    [Fact]
    public void CommitOverMembers_StampsTrailer_AndSkipsMembersWithNothingStaged()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        // b has nothing staged — it must be skipped, not committed.
        git.Staged[a.Id] = 2;
        git.Staged[b.Id] = 0;
        git.Staged[c.Id] = 1;
        var full = ChangeSetOperations.StampTrailer("shared message", "feature/x");

        var result = ChangeSetOperations.CommitOverMembers(
            new[] { a, b, c },
            hasStaged: r => git.Staged.TryGetValue(r.Id, out var n) && n > 0,
            commit: r => git.Commit(r, full, amend: false));

        // Only a and c committed; b contributed no outcome at all (it didn't commit).
        Assert.True(result.AllSucceeded);
        Assert.Equal(new[] { a.Id, c.Id }, result.Results.Select(x => x.RepoId));
        Assert.Equal(new[] { a.Id, c.Id }, git.CommitCalls.Select(x => x.RepoId));
        // Every commit carries the Change-Set trailer.
        Assert.All(git.CommitCalls, x => Assert.Contains("\n\nChange-Set: feature/x", x.Message));
        Assert.All(git.CommitCalls, x => Assert.False(x.Amend)); // 5.5: never amends across repos
    }

    [Fact]
    public void CommitOverMembers_OneCommitFails_OthersStillCommit_NoRollback()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");
        var c = Repo("c");
        git.Staged[a.Id] = 1;
        git.Staged[b.Id] = 1;
        git.Staged[c.Id] = 1;
        git.Results[b.Id] = new GitOutcome.Failed("pre-commit hook failed");
        var full = ChangeSetOperations.StampTrailer("msg", "feature/x");

        var result = ChangeSetOperations.CommitOverMembers(
            new[] { a, b, c },
            hasStaged: r => git.Staged.TryGetValue(r.Id, out var n) && n > 0,
            commit: r => git.Commit(r, full, amend: false));

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.IsType<GitOutcome.Success>(result.Results[0].Outcome);
        var failed = Assert.IsType<GitOutcome.Failed>(result.Results[1].Outcome);
        Assert.Contains("hook failed", failed.Message);
        Assert.IsType<GitOutcome.Success>(result.Results[2].Outcome);
        // c committed despite b's failure — no rollback, a failed member never blocks the rest.
        Assert.Contains(git.CommitCalls, x => x.RepoId == c.Id);
    }

    [Fact]
    public void CommitOverMembers_NothingStagedAnywhere_IsVacuousSuccess_NoCommits()
    {
        var git = new FakeGitService();
        var a = Repo("a");
        var b = Repo("b");

        var result = ChangeSetOperations.CommitOverMembers(
            new[] { a, b },
            hasStaged: _ => false,
            commit: r => git.Commit(r, "m", amend: false));

        Assert.Empty(result.Results);
        Assert.True(result.AllSucceeded);
        Assert.Empty(git.CommitCalls);
    }

    // A fake IGitService whose batch calls (Push / Fetch / CheckoutLocalBranch / DeleteBranch) return
    // a scripted per-repo GitOutcome (Ok by default) and record the call, so tests can assert both the
    // outcome mapping and that every member was actually invoked. Any repo id in ThrowFor makes its
    // call throw, exercising the coordinator's exception-folding. Everything outside the batch surface
    // throws — the coordinator never touches it.
    private sealed class FakeGitService : IGitService
    {
        public Dictionary<Guid, GitOutcome> Results { get; } = new();
        public HashSet<Guid> ThrowFor { get; } = new();
        public List<(string Op, Guid RepoId)> Calls { get; } = new();

        // Full-argument capture for CreateBranch, so the Phase-4 create tests can assert the per-repo
        // start point the coordinator maps in (not just that the member was touched).
        public List<(Guid RepoId, string Name, string StartPoint, bool Checkout)> CreateCalls { get; } = new();

        // Phase 5.4: staged-file count per repo (drives skip-unstaged) and captured commit calls (so the
        // trailer stamping and per-repo commit are assertable).
        public Dictionary<Guid, int> Staged { get; } = new();
        public List<(Guid RepoId, string Message, bool Amend)> CommitCalls { get; } = new();

        private GitOutcome Record(string op, Repo repo)
        {
            Calls.Add((op, repo.Id));
            if (ThrowFor.Contains(repo.Id)) throw new InvalidOperationException("boom");
            return Results.TryGetValue(repo.Id, out var outcome) ? outcome : GitOutcome.Ok;
        }

        public GitOutcome Push(Repo repo, bool force = false) => Record("push", repo);
        public GitOutcome Fetch(Repo repo) => Record("fetch", repo);
        public GitOutcome CheckoutLocalBranch(Repo repo, string branchName) => Record("checkout", repo);
        public GitOutcome DeleteBranch(Repo repo, string name, bool force) => Record("delete", repo);
        public GitOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout)
        {
            CreateCalls.Add((repo.Id, name, startPoint, checkout));
            return Record("create", repo);
        }

        // --- everything else is outside the batch surface ---
        public Fetched<CommitSnapshot> Load(Repo repo, int cap) => throw new NotImplementedException();
        public Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap) => throw new NotImplementedException();
        public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha) => throw new NotImplementedException();
        public string? MergeBase(Repo repo, string a, string b) => throw new NotImplementedException();
        public ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef) => throw new NotImplementedException();
        public Fetched<CommitDetails> LoadDetails(Repo repo, string sha) => throw new NotImplementedException();
        public Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo)
        {
            var count = Staged.TryGetValue(repo.Id, out var n) ? n : 0;
            var staged = new List<FileChange>(count);
            for (var i = 0; i < count; i++) staged.Add(new FileChange($"file{i}.cs", null, FileChangeStatus.Modified));
            return new LocalChangesSnapshot(repo.Id, staged, Array.Empty<FileChange>());
        }
        public GitStatusSummary? GetStatusSummary(Repo repo) => throw new NotImplementedException();
        public Fetched<BranchListing> GetBranches(Repo repo) => throw new NotImplementedException();
        public string? GetDefaultBranchName(Repo repo) => throw new NotImplementedException();
        public GitOutcome Stage(Repo repo, IReadOnlyList<string> paths) => throw new NotImplementedException();
        public GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths) => throw new NotImplementedException();
        public GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths) => throw new NotImplementedException();
        public GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths) => throw new NotImplementedException();
        public GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse) => throw new NotImplementedException();
        public GitOutcome Commit(Repo repo, string message, bool amend)
        {
            CommitCalls.Add((repo.Id, message, amend));
            return Record("commit", repo);
        }
        public HeadCommitMessage? GetHeadCommitMessage(Repo repo) => throw new NotImplementedException();
        public IReadOnlyList<FileChange> GetAmendStagedFiles(Repo repo) => throw new NotImplementedException();
        public PushStatus GetPushStatus(Repo repo) => throw new NotImplementedException();
        public DetachedHeadReport GetDetachedHeadReport(Repo repo) => throw new NotImplementedException();
        public GitOutcome AttachDetachedHead(Repo repo, string branch) => throw new NotImplementedException();
        public GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream) => throw new NotImplementedException();
        public IReadOnlyList<string> GetRemoteNames(Repo repo) => throw new NotImplementedException();
        public string? GetRemoteUrl(Repo repo, string remoteName) => throw new NotImplementedException();
        public GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config) => throw new NotImplementedException();
        public GitOutcome EditRemote(Repo repo, string oldName, string newName, string url) => throw new NotImplementedException();
        public GitOutcome AddRemote(Repo repo, string name, string url) => throw new NotImplementedException();
        public PullOutcome Pull(Repo repo, PullStrategy? strategy = null) => throw new NotImplementedException();
        public CloneOutcome Clone(string url, string targetPath, Action<string>? onLine = null) => throw new NotImplementedException();
        public GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null) => throw new NotImplementedException();
        public GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track) => throw new NotImplementedException();
        public GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode) => throw new NotImplementedException();
        public GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout) => throw new NotImplementedException();
        public bool IsAncestor(Repo repo, string maybeAncestor, string descendant) => throw new NotImplementedException();
        public GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes) => throw new NotImplementedException();
        public GitOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes) => throw new NotImplementedException();
        public GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force) => throw new NotImplementedException();
        public GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName) => throw new NotImplementedException();
        public GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths) => throw new NotImplementedException();
        public MergeLikeOutcome ApplyStash(Repo repo, int index) => throw new NotImplementedException();
        public GitOutcome DropStash(Repo repo, int index) => throw new NotImplementedException();
        public GitOutcome RenameStash(Repo repo, int index, string newMessage) => throw new NotImplementedException();
        public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null) => throw new NotImplementedException();
        public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null) => throw new NotImplementedException();
        public RepoOperationState GetOperationState(Repo repo) => throw new NotImplementedException();
        public RepoOperation? GetOperation(Repo repo) => throw new NotImplementedException();
        public bool HasUnmergedPaths(Repo repo) => throw new NotImplementedException();
        public string? GetMergeMessage(Repo repo) => throw new NotImplementedException();
        public AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false) => throw new NotImplementedException();
        public ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state) => throw new NotImplementedException();
        public ContinueOutcome SkipOperation(Repo repo, RepoOperationState state) => throw new NotImplementedException();
        public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary) => throw new NotImplementedException();
        public GitOutcome AddWorktree(Repo primary, WorktreeAddRequest request) => throw new NotImplementedException();
        public GitOutcome RemoveWorktree(Repo primary, string worktreePath, bool force) => throw new NotImplementedException();
        public GitOutcome PruneWorktrees(Repo primary) => throw new NotImplementedException();
        public IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary) => throw new NotImplementedException();
        public GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request) => throw new NotImplementedException();
        public MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request) => throw new NotImplementedException();
        public GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force) => throw new NotImplementedException();
        public bool StageSubmodulePointer(Repo parent, string relativePath) => throw new NotImplementedException();
        public IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha) => throw new NotImplementedException();
        public MergePreviewResult PreviewMerge(Repo repo, string sourceRef) => throw new NotImplementedException();
        public MergeLikeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy) => throw new NotImplementedException();
        public RebasePreviewResult PreviewRebase(Repo repo, string targetRef) => throw new NotImplementedException();
        public MergeLikeOutcome Rebase(Repo repo, string targetRef, bool autostash) => throw new NotImplementedException();
        public MergeLikeOutcome CherryPick(Repo repo, string commitSha) => throw new NotImplementedException();
        public MergeLikeOutcome RevertCommit(Repo repo, string commitSha) => throw new NotImplementedException();
        public GitOutcome TakeOurs(Repo repo, string path) => throw new NotImplementedException();
        public GitOutcome TakeTheirs(Repo repo, string path) => throw new NotImplementedException();
        public GitOutcome TakeBoth(Repo repo, string path) => throw new NotImplementedException();
        public GitOutcome MarkResolved(Repo repo, string path) => throw new NotImplementedException();
        public ConflictContext? GetConflictContext(Repo repo, string path) => throw new NotImplementedException();
    }
}
