using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Submodules;
using GitBench.Git;

namespace GitBench.Tests;

// Wraps a real GitService and counts GetStatusSummary calls, so a test can assert which triggers
// actually run the summary probe (vs. rely on RepoSnapshotStore's ingest). It also records the
// GitRef CreateBranch was handed, so a test can assert what a view model actually sent git rather
// than only what the resulting branch points at. Every other member delegates unchanged. The
// counters use Interlocked because probes run off the UI thread.
//
// It implements only the capabilities its subjects ask for — the status/working-tree/conflict/
// stash/submodule/branch/config facets — not the whole of git. Adding a facet here is the signal
// that a new subject was pointed at it.
internal sealed class CountingGitService(IGitService inner) :
    IGitStatusReader,
    IGitWorkingTreeOperations,
    IGitConflictOperations,
    IGitStashOperations,
    IGitSubmoduleOperations,
    IGitBranchOperations,
    IGitConfigOperations
{
    // The start point of the last CreateBranch call, or null if there hasn't been one.
    public GitRef? LastCreateBranchStartPoint { get; private set; }

    private int _statusSummaryCalls;
    private int _syncSummaryCalls;
    private int _localChangesCalls;
    private int _headMessageCalls;
    private int _amendStagedCalls;
    private int _applyUntrackedCacheCalls;
    public int StatusSummaryCalls => Volatile.Read(ref _statusSummaryCalls);
    public int SyncSummaryCalls => Volatile.Read(ref _syncSummaryCalls);
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

    public GitSyncSummary? GetSyncSummary(Repo repo)
    {
        Interlocked.Increment(ref _syncSummaryCalls);
        return inner.GetSyncSummary(repo);
    }

    public Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo)
    {
        Interlocked.Increment(ref _localChangesCalls);
        if (ThrowOnReads) throw new InvalidOperationException("GetLocalChanges must not run here.");
        return inner.GetLocalChanges(repo);
    }

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

    public GitOutcome CreateBranch(Repo repo, string name, GitRef startPoint, bool checkout)
    {
        LastCreateBranchStartPoint = startPoint;
        return inner.CreateBranch(repo, name, startPoint, checkout);
    }

    // ---- everything else delegates ----
    public DetachedHeadReport GetDetachedHeadReport(Repo repo) => inner.GetDetachedHeadReport(repo);
    public RepoOperationState GetOperationState(Repo repo) => inner.GetOperationState(repo);
    public RepoOperation? GetOperation(Repo repo) => inner.GetOperation(repo);
    public bool HasUnmergedPaths(Repo repo) => inner.HasUnmergedPaths(repo);
    public string? GetMergeMessage(Repo repo) => inner.GetMergeMessage(repo);

    public GitOutcome Stage(Repo repo, IReadOnlyList<string> paths) => inner.Stage(repo, paths);
    public GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths) => inner.Unstage(repo, paths);
    public GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths) => inner.ResetToParent(repo, paths);
    public GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths) => inner.DiscardChanges(repo, paths);
    public GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse) => inner.ApplyPatch(repo, patch, cached, reverse);
    public GitOutcome Commit(Repo repo, string message, bool amend) => inner.Commit(repo, message, amend);

    public GitOutcome TakeOurs(Repo repo, string path) => inner.TakeOurs(repo, path);
    public GitOutcome TakeTheirs(Repo repo, string path) => inner.TakeTheirs(repo, path);
    public GitOutcome TakeBoth(Repo repo, string path) => inner.TakeBoth(repo, path);
    public GitOutcome MarkResolved(Repo repo, string path) => inner.MarkResolved(repo, path);
    public ConflictContext? GetConflictContext(Repo repo, string path) => inner.GetConflictContext(repo, path);
    public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo) => inner.GetConflictedPaths(repo);
    public ConflictStages? GetConflictStages(Repo repo, string path) => inner.GetConflictStages(repo, path);

    public GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths) => inner.CreateStash(repo, message, includeUntracked, keepIndex, paths);
    public MergeLikeOutcome ApplyStash(Repo repo, int index) => inner.ApplyStash(repo, index);
    public GitOutcome DropStash(Repo repo, int index) => inner.DropStash(repo, index);
    public GitOutcome RenameStash(Repo repo, int index, string newMessage) => inner.RenameStash(repo, index, newMessage);

    public IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary) => inner.ListSubmodules(primary);
    public GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request) => inner.AddSubmodule(primary, request);
    public MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request) => inner.UpdateSubmodules(primary, request);
    public GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force) => inner.DeinitSubmodule(primary, submodulePath, force);
    public bool StageSubmodulePointer(Repo parent, string relativePath) => inner.StageSubmodulePointer(parent, relativePath);
    public IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha) => inner.GetSubmodulePointerChanges(repo, commitSha);

    public Fetched<BranchListing> GetBranches(Repo repo) => inner.GetBranches(repo);
    public GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force) => inner.RenameBranch(repo, oldName, newName, force);
    public GitOutcome DeleteBranch(Repo repo, string name, bool force) => inner.DeleteBranch(repo, name, force);
    public GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName) => inner.DeleteRemoteBranch(repo, remoteName, branchName);
    public GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout) => inner.MoveBranch(repo, branchName, commitSha, checkout);
    public GitOutcome CheckoutLocalBranch(Repo repo, string branchName) => inner.CheckoutLocalBranch(repo, branchName);
    public GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track) => inner.CheckoutRemoteBranch(repo, localName, remoteName, remoteBranchName, track);
    public GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null) => inner.FastForwardBranch(repo, localBranch, remoteName, remoteBranch, onLine);
    public GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream) => inner.PublishBranch(repo, localBranch, remoteName, remoteBranchName, setUpstream);
    public GitOutcome AttachDetachedHead(Repo repo, string branch) => inner.AttachDetachedHead(repo, branch);
    public GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode) => inner.ResetCurrent(repo, commitSha, mode);

    public GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config) => inner.PinLocalIdentity(repo, config);
}
