using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;

namespace GitBench.Tests;

// Wraps a real IGitService and counts GetStatusSummary calls, so a test can assert which triggers
// actually run the summary probe (vs. rely on RepoSnapshotStore's ingest). Every other member
// delegates unchanged. The counter uses Interlocked because probes run off the UI thread.
internal sealed class CountingGitService(IGitService inner) : IGitService
{
    private int _statusSummaryCalls;
    private int _localChangesCalls;
    private int _headMessageCalls;
    private int _amendStagedCalls;
    private int _applyUntrackedCacheCalls;
    public int StatusSummaryCalls => Volatile.Read(ref _statusSummaryCalls);
    public int GetLocalChangesCalls => Volatile.Read(ref _localChangesCalls);
    public int GetHeadCommitMessageCalls => Volatile.Read(ref _headMessageCalls);
    public int GetAmendStagedFilesCalls => Volatile.Read(ref _amendStagedCalls);
    public int ApplyUntrackedCacheCalls => Volatile.Read(ref _applyUntrackedCacheCalls);

    // The repos ApplyUntrackedCache was invoked on, in call order — lets a test assert which rows
    // got tuned (primaries) and which were skipped (worktrees/submodules). Guarded because applies
    // run off the UI thread (Task.Run).
    private readonly List<Repo> _appliedUntrackedCache = new();
    public IReadOnlyList<Repo> AppliedUntrackedCache
    {
        get { lock (_appliedUntrackedCache) return _appliedUntrackedCache.ToList(); }
    }

    // When set, ApplyUntrackedCache throws instead of delegating — lets a test prove the service
    // never touches it while the preference is off.
    public bool ThrowOnApplyUntrackedCache { get; set; }

    // When set, the three read paths throw instead of delegating — lets a test prove a code path
    // (e.g. a dialog constructor) never issues a status/head/amend read.
    public bool ThrowOnReads { get; set; }

    // When set, GetAmendStagedFiles parks here before counting or running, so a test can assert the
    // deferred amend diff hasn't run yet and then release it deterministically.
    public ManualResetEventSlim? AmendStagedGate { get; set; }

    public GitStatusSummary? GetStatusSummary(Repo repo)
    {
        Interlocked.Increment(ref _statusSummaryCalls);
        return inner.GetStatusSummary(repo);
    }

    // ---- everything else delegates ----
    public Fetched<CommitSnapshot> Load(Repo repo, int cap) => inner.Load(repo, cap);
    public Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap) => inner.LoadReviewStack(repo, baseRef, headRef, cap);
    public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha) => inner.LoadRangeFiles(repo, baseSha, headSha);
    public string? MergeBase(Repo repo, string a, string b) => inner.MergeBase(repo, a, b);
    public ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef) => inner.ResolveAutoReviewBase(repo, headRef);
    public Fetched<CommitDetails> LoadDetails(Repo repo, string sha) => inner.LoadDetails(repo, sha);
    public Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo)
    {
        Interlocked.Increment(ref _localChangesCalls);
        if (ThrowOnReads) throw new InvalidOperationException("GetLocalChanges must not run here.");
        return inner.GetLocalChanges(repo);
    }
    public Fetched<BranchListing> GetBranches(Repo repo) => inner.GetBranches(repo);
    public GitOutcome Stage(Repo repo, IReadOnlyList<string> paths) => inner.Stage(repo, paths);
    public GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths) => inner.Unstage(repo, paths);
    public GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths) => inner.ResetToParent(repo, paths);
    public GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths) => inner.DiscardChanges(repo, paths);
    public GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse) => inner.ApplyPatch(repo, patch, cached, reverse);
    public GitOutcome Commit(Repo repo, string message, bool amend) => inner.Commit(repo, message, amend);
    public HeadCommitMessage? GetHeadCommitMessage(Repo repo)
    {
        Interlocked.Increment(ref _headMessageCalls);
        if (ThrowOnReads) throw new InvalidOperationException("GetHeadCommitMessage must not run here.");
        return inner.GetHeadCommitMessage(repo);
    }
    public IReadOnlyList<FileChange> GetAmendStagedFiles(Repo repo)
    {
        AmendStagedGate?.Wait();
        Interlocked.Increment(ref _amendStagedCalls);
        if (ThrowOnReads) throw new InvalidOperationException("GetAmendStagedFiles must not run here.");
        return inner.GetAmendStagedFiles(repo);
    }
    public DetachedHeadReport GetDetachedHeadReport(Repo repo) => inner.GetDetachedHeadReport(repo);
    public GitOutcome AttachDetachedHead(Repo repo, string branch) => inner.AttachDetachedHead(repo, branch);
    public GitOutcome Push(Repo repo, bool force = false) => inner.Push(repo, force);
    public GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream) => inner.PublishBranch(repo, localBranch, remoteName, remoteBranchName, setUpstream);
    public IReadOnlyList<string> GetRemoteNames(Repo repo) => inner.GetRemoteNames(repo);
    public string? GetRemoteUrl(Repo repo, string remoteName) => inner.GetRemoteUrl(repo, remoteName);
    public GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config) => inner.PinLocalIdentity(repo, config);
    public GitOutcome ApplyUntrackedCache(Repo repo)
    {
        Interlocked.Increment(ref _applyUntrackedCacheCalls);
        if (ThrowOnApplyUntrackedCache) throw new InvalidOperationException("ApplyUntrackedCache must not run here.");
        var outcome = inner.ApplyUntrackedCache(repo);
        // Recorded only after the real write completes, so a test that waits on this list sees a
        // finished config write (the call counter above fires at entry, before the slow probe).
        lock (_appliedUntrackedCache) _appliedUntrackedCache.Add(repo);
        return outcome;
    }
    public GitOutcome EditRemote(Repo repo, string oldName, string newName, string url) => inner.EditRemote(repo, oldName, newName, url);
    public GitOutcome AddRemote(Repo repo, string name, string url) => inner.AddRemote(repo, name, url);
    public PullOutcome Pull(Repo repo, PullStrategy? strategy = null) => inner.Pull(repo, strategy);
    public GitOutcome Fetch(Repo repo) => inner.Fetch(repo);
    public CloneOutcome Clone(string url, string targetPath, Action<string>? onLine = null) => inner.Clone(url, targetPath, onLine);
    public GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null) => inner.FastForwardBranch(repo, localBranch, remoteName, remoteBranch, onLine);
    public GitOutcome CheckoutLocalBranch(Repo repo, string branchName) => inner.CheckoutLocalBranch(repo, branchName);
    public GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track) => inner.CheckoutRemoteBranch(repo, localName, remoteName, remoteBranchName, track);
    public GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode) => inner.ResetCurrent(repo, commitSha, mode);
    public GitOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout) => inner.CreateBranch(repo, name, startPoint, checkout);
    public GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout) => inner.MoveBranch(repo, branchName, commitSha, checkout);
    public bool IsAncestor(Repo repo, string maybeAncestor, string descendant) => inner.IsAncestor(repo, maybeAncestor, descendant);
    public GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes) => inner.CreateTag(repo, name, message, commitSha, pushToAllRemotes);
    public GitOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes) => inner.DeleteTag(repo, name, deleteFromRemotes);
    public GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force) => inner.RenameBranch(repo, oldName, newName, force);
    public GitOutcome DeleteBranch(Repo repo, string name, bool force) => inner.DeleteBranch(repo, name, force);
    public GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName) => inner.DeleteRemoteBranch(repo, remoteName, branchName);
    public GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths) => inner.CreateStash(repo, message, includeUntracked, keepIndex, paths);
    public MergeLikeOutcome ApplyStash(Repo repo, int index) => inner.ApplyStash(repo, index);
    public GitOutcome DropStash(Repo repo, int index) => inner.DropStash(repo, index);
    public GitOutcome RenameStash(Repo repo, int index, string newMessage) => inner.RenameStash(repo, index, newMessage);
    public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null) => inner.GetDiff(repo, path, side, commitSha, baseSha);
    public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null) => inner.GetFileText(repo, path, side, oldSide, commitSha, baseSha);
    public RepoOperationState GetOperationState(Repo repo) => inner.GetOperationState(repo);
    public RepoOperation? GetOperation(Repo repo) => inner.GetOperation(repo);
    public bool HasUnmergedPaths(Repo repo) => inner.HasUnmergedPaths(repo);
    public string? GetMergeMessage(Repo repo) => inner.GetMergeMessage(repo);
    public AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false) => inner.AbortOperation(repo, state, forceQuit);
    public ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state) => inner.ContinueOperation(repo, state);
    public ContinueOutcome SkipOperation(Repo repo, RepoOperationState state) => inner.SkipOperation(repo, state);
    public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary) => inner.ListWorktrees(primary);
    public GitOutcome AddWorktree(Repo primary, WorktreeAddRequest request) => inner.AddWorktree(primary, request);
    public GitOutcome RemoveWorktree(Repo primary, string worktreePath, bool force) => inner.RemoveWorktree(primary, worktreePath, force);
    public GitOutcome UnlockWorktree(Repo primary, string worktreePath) => inner.UnlockWorktree(primary, worktreePath);
    public GitOutcome PruneWorktrees(Repo primary) => inner.PruneWorktrees(primary);
    public IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary) => inner.ListSubmodules(primary);
    public GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request) => inner.AddSubmodule(primary, request);
    public MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request) => inner.UpdateSubmodules(primary, request);
    public GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force) => inner.DeinitSubmodule(primary, submodulePath, force);
    public bool StageSubmodulePointer(Repo parent, string relativePath) => inner.StageSubmodulePointer(parent, relativePath);
    public IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha) => inner.GetSubmodulePointerChanges(repo, commitSha);
    public MergePreviewResult PreviewMerge(Repo repo, string sourceRef) => inner.PreviewMerge(repo, sourceRef);
    public MergeLikeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy) => inner.Merge(repo, sourceRef, strategy);
    public RebasePreviewResult PreviewRebase(Repo repo, string targetRef) => inner.PreviewRebase(repo, targetRef);
    public MergeLikeOutcome Rebase(Repo repo, string targetRef, bool autostash) => inner.Rebase(repo, targetRef, autostash);
    public MergeLikeOutcome CherryPick(Repo repo, string commitSha) => inner.CherryPick(repo, commitSha);
    public MergeLikeOutcome RevertCommit(Repo repo, string commitSha) => inner.RevertCommit(repo, commitSha);
    public GitOutcome TakeOurs(Repo repo, string path) => inner.TakeOurs(repo, path);
    public GitOutcome TakeTheirs(Repo repo, string path) => inner.TakeTheirs(repo, path);
    public GitOutcome TakeBoth(Repo repo, string path) => inner.TakeBoth(repo, path);
    public GitOutcome MarkResolved(Repo repo, string path) => inner.MarkResolved(repo, path);
    public ConflictContext? GetConflictContext(Repo repo, string path) => inner.GetConflictContext(repo, path);
}
