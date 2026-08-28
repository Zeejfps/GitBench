using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;
using GitBench.Git;

namespace GitBench.Tests;

// Wraps a real IGitService and lets a test script the three remote operations, so a store test can
// ask what happens on a diverged pull or a fetch that throws without owning a remote that behaves
// that way. Everything else delegates unchanged.
internal sealed class ScriptedRemoteGitService(IGitService inner) : IGitService
{
    private int _fetchCalls;
    private int _pullCalls;
    private int _pushCalls;
    private int _conflictListings;
    private int _conflictContexts;
    private readonly List<PullStrategy?> _pullStrategies = new();
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public int FetchCalls => Volatile.Read(ref _fetchCalls);
    public int PullCalls => Volatile.Read(ref _pullCalls);
    public int PushCalls => Volatile.Read(ref _pushCalls);

    // How many times the whole unmerged list was asked for, and how many times a single path's
    // context was — the difference between one listing and one process per conflicted file.
    public int ConflictListings => Volatile.Read(ref _conflictListings);
    public int ConflictContexts => Volatile.Read(ref _conflictContexts);

    /// <summary>
    /// Holds every remote call open at the point it has reached git but not yet returned, until the
    /// handle is disposed. Lets a test put a second call squarely in the already-running case instead
    /// of racing the first one's completion.
    /// </summary>
    public IDisposable HoldRemoteCalls()
    {
        _open.Reset();
        return new Release(_open);
    }

    private void WaitWhileHeld()
    {
        // A bounded wait: a test that forgets to release should fail on its own assertion rather
        // than wedge the run.
        if (!_open.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("A held remote call was never released.");
    }

    private sealed class Release(ManualResetEventSlim open) : IDisposable
    {
        public void Dispose() => open.Set();
    }

    // The strategy each pull was asked for, in call order — null means "git's configured default".
    public IReadOnlyList<PullStrategy?> PullStrategies
    {
        get { lock (_pullStrategies) return _pullStrategies.ToList(); }
    }

    public Func<Repo, GitOutcome>? OnFetch { get; set; }
    public Func<Repo, PullStrategy?, PullOutcome>? OnPull { get; set; }
    public Func<Repo, bool, GitOutcome>? OnPush { get; set; }

    public GitOutcome Fetch(Repo repo)
    {
        Interlocked.Increment(ref _fetchCalls);
        WaitWhileHeld();
        return OnFetch is { } scripted ? scripted(repo) : inner.Fetch(repo);
    }

    public PullOutcome Pull(Repo repo, PullStrategy? strategy = null)
    {
        Interlocked.Increment(ref _pullCalls);
        lock (_pullStrategies) _pullStrategies.Add(strategy);
        WaitWhileHeld();
        return OnPull is { } scripted ? scripted(repo, strategy) : inner.Pull(repo, strategy);
    }

    public GitOutcome Push(Repo repo, bool force = false)
    {
        Interlocked.Increment(ref _pushCalls);
        WaitWhileHeld();
        return OnPush is { } scripted ? scripted(repo, force) : inner.Push(repo, force);
    }

    // ---- everything else delegates ----
    public Fetched<CommitSnapshot> Load(Repo repo, int cap) => inner.Load(repo, cap);
    public Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap) => inner.LoadReviewStack(repo, baseRef, headRef, cap);
    public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha) => inner.LoadRangeFiles(repo, baseSha, headSha);
    public string? MergeBase(Repo repo, string a, string b) => inner.MergeBase(repo, a, b);
    public ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef) => inner.ResolveAutoReviewBase(repo, headRef);
    public Fetched<CommitDetails> LoadDetails(Repo repo, string sha) => inner.LoadDetails(repo, sha);
    public Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo) => inner.GetLocalChanges(repo);
    public GitStatusSummary? GetStatusSummary(Repo repo) => inner.GetStatusSummary(repo);
    public GitSyncSummary? GetSyncSummary(Repo repo) => inner.GetSyncSummary(repo);
    public Fetched<BranchListing> GetBranches(Repo repo) => inner.GetBranches(repo);
    public GitOutcome Stage(Repo repo, IReadOnlyList<string> paths) => inner.Stage(repo, paths);
    public GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths) => inner.Unstage(repo, paths);
    public GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths) => inner.ResetToParent(repo, paths);
    public GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths) => inner.DiscardChanges(repo, paths);
    public GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse) => inner.ApplyPatch(repo, patch, cached, reverse);
    public GitOutcome Commit(Repo repo, string message, bool amend) => inner.Commit(repo, message, amend);
    public HeadCommitMessage? GetHeadCommitMessage(Repo repo) => inner.GetHeadCommitMessage(repo);
    public IReadOnlyList<FileChange> GetAmendStagedFiles(Repo repo) => inner.GetAmendStagedFiles(repo);
    public DetachedHeadReport GetDetachedHeadReport(Repo repo) => inner.GetDetachedHeadReport(repo);
    public GitOutcome AttachDetachedHead(Repo repo, string branch) => inner.AttachDetachedHead(repo, branch);
    public GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream) => inner.PublishBranch(repo, localBranch, remoteName, remoteBranchName, setUpstream);
    public IReadOnlyList<string> GetRemoteNames(Repo repo) => inner.GetRemoteNames(repo);
    public string? GetRemoteUrl(Repo repo, string remoteName) => inner.GetRemoteUrl(repo, remoteName);
    public GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config) => inner.PinLocalIdentity(repo, config);
    public GitOutcome ApplyUntrackedCache(Repo repo) => inner.ApplyUntrackedCache(repo);
    public GitOutcome EditRemote(Repo repo, string oldName, string newName, string url) => inner.EditRemote(repo, oldName, newName, url);
    public GitOutcome AddRemote(Repo repo, string name, string url) => inner.AddRemote(repo, name, url);
    public CloneOutcome Clone(string url, string targetPath, LocalIdentityConfig? identity = null, Action<string>? onLine = null) => inner.Clone(url, targetPath, identity, onLine);
    public GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null) => inner.FastForwardBranch(repo, localBranch, remoteName, remoteBranch, onLine);
    public GitOutcome CheckoutLocalBranch(Repo repo, string branchName) => inner.CheckoutLocalBranch(repo, branchName);
    public GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track) => inner.CheckoutRemoteBranch(repo, localName, remoteName, remoteBranchName, track);
    public GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode) => inner.ResetCurrent(repo, commitSha, mode);
    public GitOutcome CreateBranch(Repo repo, string name, GitRef startPoint, bool checkout) => inner.CreateBranch(repo, name, startPoint, checkout);
    public GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout) => inner.MoveBranch(repo, branchName, commitSha, checkout);
    public bool IsAncestor(Repo repo, string maybeAncestor, string descendant) => inner.IsAncestor(repo, maybeAncestor, descendant);
    public GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes) => inner.CreateTag(repo, name, message, commitSha, pushToAllRemotes);
    public GitOutcome PushTag(Repo repo, string name, string? remoteName = null) => inner.PushTag(repo, name, remoteName);
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
    public byte[]? GetFileBytes(Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null, string? baseSha = null) => inner.GetFileBytes(repo, path, side, oldSide, maxBytes, commitSha, baseSha);
    public bool IsPathTracked(Repo repo, string relativePath) => inner.IsPathTracked(repo, relativePath);
    public bool IsPathIgnored(Repo repo, string relativePath) => inner.IsPathIgnored(repo, relativePath);
    public IReadOnlyList<string> ListTrackedFiles(Repo repo) => inner.ListTrackedFiles(repo);
    public RepoOperationState GetOperationState(Repo repo) => inner.GetOperationState(repo);
    public RepoOperation? GetOperation(Repo repo) => inner.GetOperation(repo);
    public bool HasUnmergedPaths(Repo repo) => inner.HasUnmergedPaths(repo);
    public string? GetMergeMessage(Repo repo) => inner.GetMergeMessage(repo);
    public AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false) => inner.AbortOperation(repo, state, forceQuit);
    public ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state) => inner.ContinueOperation(repo, state);
    public ContinueOutcome SkipOperation(Repo repo, RepoOperationState state) => inner.SkipOperation(repo, state);
    public IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary) => inner.ListWorktrees(primary);
    public GitOutcome AddWorktree(Repo primary, WorktreeAddRequest request) => inner.AddWorktree(primary, request);
    public WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force) => inner.RemoveWorktree(primary, worktreePath, force);
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
    public ConflictContext? GetConflictContext(Repo repo, string path)
    {
        Interlocked.Increment(ref _conflictContexts);
        return inner.GetConflictContext(repo, path);
    }

    public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo)
    {
        Interlocked.Increment(ref _conflictListings);
        return inner.GetConflictedPaths(repo);
    }

    public ConflictStages? GetConflictStages(Repo repo, string path) => inner.GetConflictStages(repo, path);
}
