using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Identity;
using GitBench.Features.LocalChanges;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Features.Worktrees;

namespace GitBench.Git;

public interface IGitService
{
    Fetched<CommitSnapshot> Load(Repo repo, int cap);
    // Lists base..head as a linear review stack — the first-parent commits reachable from head
    // but not base, oldest→newest. base/head accept any ref or SHA; the returned stack carries
    // their resolved SHAs and short-sha labels (the caller overrides labels with branch names).
    Fetched<ReviewStack> LoadReviewStack(Repo repo, string baseRef, string headRef, int cap);
    // The combined net file list of a review range (base→head as one diff), for the Review window's
    // Combined mode. base/head are resolved SHAs; the list is the same FileChange shape as a commit's.
    Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha);
    // The merge-base (common-ancestor) SHA of two refs/SHAs, or null when none exists (unrelated
    // histories) or git fails. Anchors a review range's base at the divergence point.
    string? MergeBase(Repo repo, string a, string b);
    // The default review base for headRef when no explicit base is pinned: the merge-base with the
    // branch's upstream, else with the repo's default branch — carrying the ref name + kind it came
    // from (so the header can name it). Null when neither resolves.
    ResolvedReviewBase? ResolveAutoReviewBase(Repo repo, string headRef);
    Fetched<CommitDetails> LoadDetails(Repo repo, string sha);
    Fetched<LocalChangesSnapshot> GetLocalChanges(Repo repo);
    GitStatusSummary GetStatusSummary(Repo repo);
    Fetched<BranchListing> GetBranches(Repo repo);
    GitOutcome Stage(Repo repo, IReadOnlyList<string> paths);
    GitOutcome Unstage(Repo repo, IReadOnlyList<string> paths);
    GitOutcome ResetToParent(Repo repo, IReadOnlyList<string> paths);
    GitOutcome DiscardChanges(Repo repo, IReadOnlyList<string> paths);
    GitOutcome ApplyPatch(Repo repo, string patch, bool cached, bool reverse);
    GitOutcome Commit(Repo repo, string message, bool amend);
    HeadCommitMessage? GetHeadCommitMessage(Repo repo);
    IReadOnlyList<FileChange> GetHeadCommitFiles(Repo repo);
    PushStatus GetPushStatus(Repo repo);
    bool IsHeadDetachedAtRisk(Repo repo);
    GitOutcome Push(Repo repo, bool force = false);
    GitOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream);
    IReadOnlyList<string> GetRemoteNames(Repo repo);
    string? GetRemoteUrl(Repo repo, string remoteName);
    GitOutcome PinLocalIdentity(Repo repo, LocalIdentityConfig config);
    GitOutcome EditRemote(Repo repo, string oldName, string newName, string url);
    GitOutcome AddRemote(Repo repo, string name, string url);
    PullOutcome Pull(Repo repo, PullStrategy? strategy = null);
    GitOutcome Fetch(Repo repo);
    // Clones url into targetPath (a not-yet-existing or empty directory). onLine streams git's
    // progress output. On success RepoPath carries the absolute path of the new working tree.
    CloneOutcome Clone(string url, string targetPath, Action<string>? onLine = null);
    GitOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null);
    GitOutcome CheckoutLocalBranch(Repo repo, string branchName);
    GitOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track);
    GitOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode);
    GitOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout);
    GitOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout);
    bool IsAncestor(Repo repo, string maybeAncestor, string descendant);
    GitOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes);
    GitOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes);
    GitOutcome RenameBranch(Repo repo, string oldName, string newName, bool force);
    GitOutcome DeleteBranch(Repo repo, string name, bool force);
    GitOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName);
    GitOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths);
    MergeLikeOutcome ApplyStash(Repo repo, int index);
    GitOutcome DropStash(Repo repo, int index);
    GitOutcome RenameStash(Repo repo, int index, string newMessage);
    DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null);
    // Full file text for one side of a diff, used by syntax highlighting's whole-file tokenize.
    // oldSide picks the "before" content (removed lines), else the "after" content (added/
    // context). Returns null when that side has no content (root commit's parent, pure add/
    // delete) or on any failure — the caller then renders that side plain.
    string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null);
    RepoOperationState GetOperationState(Repo repo);
    RepoOperation? GetOperation(Repo repo);
    bool HasUnmergedPaths(Repo repo);
    // The default merge commit message (MERGE_MSG) when a merge is in progress, else null.
    // Used to pre-fill the commit box so committing finishes the merge.
    string? GetMergeMessage(Repo repo);
    AbortOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false);
    ContinueOutcome ContinueOperation(Repo repo, RepoOperationState state);
    ContinueOutcome SkipOperation(Repo repo, RepoOperationState state);
    IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary);
    GitOutcome AddWorktree(Repo primary, WorktreeAddRequest request);
    GitOutcome RemoveWorktree(Repo primary, string worktreePath, bool force);
    GitOutcome PruneWorktrees(Repo primary);
    IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary);
    GitOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request);
    MergeLikeOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request);
    GitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force);
    // Stages the parent's gitlink for a submodule whose HEAD has moved, so the pointer update
    // becomes a deliberate staged change instead of a lingering unstaged "modified" entry.
    // relativePath is the submodule's path within parent's working tree. Returns true when the
    // recorded pointer differed and was staged; false when it was already in sync (a no-op).
    bool StageSubmodulePointer(Repo parent, string relativePath);
    IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha);
    MergePreviewResult PreviewMerge(Repo repo, string sourceRef);
    MergeLikeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy);
    RebasePreviewResult PreviewRebase(Repo repo, string targetRef);
    MergeLikeOutcome Rebase(Repo repo, string targetRef, bool autostash);
    MergeLikeOutcome CherryPick(Repo repo, string commitSha);
    MergeLikeOutcome RevertCommit(Repo repo, string commitSha);
    // Per-file conflict resolution. TakeOurs/TakeTheirs check out the chosen side and stage
    // it; MarkResolved stages the working-tree file as-is (manual-edit path). Each returns a
    // ResolveOutcome and broadcasting is left to the caller.
    GitOutcome TakeOurs(Repo repo, string path);
    GitOutcome TakeTheirs(Repo repo, string path);
    // Resolves by keeping both sides: writes ours' content followed by theirs' content and stages.
    GitOutcome TakeBoth(Repo repo, string path);
    GitOutcome MarkResolved(Repo repo, string path);
    // Context for the conflict-resolution UI: the in-progress operation plus the ours/theirs
    // commit metadata and per-side change kind. Returns null when the path isn't conflicted.
    ConflictContext? GetConflictContext(Repo repo, string path);
}

public enum MergeStrategy
{
    Default,
    NoFastForward,
    FastForwardOnly,
    Squash,
}

public enum MergePreviewState
{
    Clean,
    Conflicts,
    Unknown,
}

public sealed record MergePreviewResult(MergePreviewState State, string? ErrorMessage);


public enum RebasePreviewState
{
    Clean,
    Conflicts,
    Unknown,
}

public sealed record RebasePreviewResult(RebasePreviewState State, string? ErrorMessage);






public enum RepoOperationState
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Bisect,
    ApplyMailbox,
    // Index has unmerged entries but no in-progress op sentinel exists. Happens after
    // `git stash apply` / `git checkout -m` / `git read-tree -m` conflict — git leaves
    // unmerged paths but doesn't write MERGE_HEAD, so the user has to resolve and stage
    // with no specific op to abort or continue.
    UnmergedPaths,
}

public sealed record HeadCommitMessage(string Title, string Description);

public sealed record PushStatus(
    string? CurrentBranchName,
    bool HasUpstream,
    int Ahead,
    int Behind,
    bool IsDetached);

// Cheap per-repo signals read from a single `git status --porcelain=v2 --branch`: the current
// branch / detached / upstream + ahead/behind (the branch header) and whether the working tree has
// any changes (any non-header record). One probe powers the RepoBar dirty dot, the toolbar's
// push/pull availability, and the status bar — for every repo, not just the active one.
public sealed record GitStatusSummary(
    string? Branch,
    bool IsDetached,
    bool HasUpstream,
    int Ahead,
    int Behind,
    bool IsDirty)
{
    public static readonly GitStatusSummary Unknown = new(null, false, false, 0, 0, false);
}



// How `git pull` reconciles a diverged branch when the default (no strategy) is rejected.
public enum PullStrategy
{
    Merge,
    Rebase,
    FastForwardOnly,
}





public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}














public enum ConflictChangeKind { Modified, Added, Deleted }

// One side of a conflict for the resolution header: a human label (branch name or short
// sha), the short sha, the commit subject + date, and what that side did to the file.
public sealed record ConflictSideInfo(
    string Label,
    string ShortSha,
    string Subject,
    DateTimeOffset When,
    ConflictChangeKind Change);

// Everything the conflict-resolution header needs: the in-progress operation and both
// sides. Ours is the current branch/HEAD; Theirs is the incoming commit (MERGE_HEAD,
// CHERRY_PICK_HEAD, REVERT_HEAD, or the rebase commit being replayed). HasBase is whether a
// common ancestor blob exists (false for add/add).
public sealed record ConflictContext(
    RepoOperationState Operation,
    ConflictSideInfo Ours,
    ConflictSideInfo Theirs,
    bool HasBase);
