namespace GitGui;

public interface IGitService
{
    CommitSnapshot Load(Repo repo, int cap);
    CommitDetails LoadDetails(Repo repo, string sha);
    LocalChangesSnapshot GetLocalChanges(Repo repo);
    BranchListing GetBranches(Repo repo);
    void Stage(Repo repo, IReadOnlyList<string> paths);
    void Unstage(Repo repo, IReadOnlyList<string> paths);
    void ResetToParent(Repo repo, IReadOnlyList<string> paths);
    string? DiscardChanges(Repo repo, IReadOnlyList<string> paths);
    string? ApplyPatch(Repo repo, string patch, bool cached, bool reverse);
    string? Commit(Repo repo, string message, bool amend);
    HeadCommitMessage? GetHeadCommitMessage(Repo repo);
    IReadOnlyList<FileChange> GetHeadCommitFiles(Repo repo);
    PushStatus GetPushStatus(Repo repo);
    bool IsHeadDetachedAtRisk(Repo repo);
    PushOutcome Push(Repo repo, bool force = false);
    PushOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream);
    IReadOnlyList<string> GetRemoteNames(Repo repo);
    string? GetRemoteUrl(Repo repo, string remoteName);
    EditRemoteOutcome EditRemote(Repo repo, string oldName, string newName, string url);
    EditRemoteOutcome AddRemote(Repo repo, string name, string url);
    PullOutcome Pull(Repo repo);
    FetchOutcome Fetch(Repo repo);
    FastForwardOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null);
    CheckoutOutcome CheckoutLocalBranch(Repo repo, string branchName);
    CheckoutOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track);
    ResetOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode);
    CreateBranchOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout);
    MoveBranchOutcome MoveBranch(Repo repo, string branchName, string commitSha, bool checkout);
    bool IsAncestor(Repo repo, string maybeAncestor, string descendant);
    CreateTagOutcome CreateTag(Repo repo, string name, string message, string commitSha, bool pushToAllRemotes);
    DeleteTagOutcome DeleteTag(Repo repo, string name, bool deleteFromRemotes);
    RenameBranchOutcome RenameBranch(Repo repo, string oldName, string newName, bool force);
    DeleteBranchOutcome DeleteBranch(Repo repo, string name, bool force);
    DeleteRemoteBranchOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName);
    StashOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths);
    StashOutcome ApplyStash(Repo repo, int index);
    StashOutcome DropStash(Repo repo, int index);
    StashOutcome RenameStash(Repo repo, int index, string newMessage);
    DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null);
    // Full file text for one side of a diff, used by syntax highlighting's whole-file tokenize.
    // oldSide picks the "before" content (removed lines), else the "after" content (added/
    // context). Returns null when that side has no content (root commit's parent, pure add/
    // delete) or on any failure — the caller then renders that side plain.
    string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null);
    RepoOperationState GetOperationState(Repo repo);
    AbortOperationOutcome AbortOperation(Repo repo, RepoOperationState state, bool forceQuit = false);
    ContinueOperationOutcome ContinueOperation(Repo repo, RepoOperationState state);
    IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary, out string? errorMessage);
    WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request);
    WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force);
    WorktreePruneOutcome PruneWorktrees(Repo primary);
    IReadOnlyList<SubmoduleInfo> ListSubmodules(Repo primary, out string? errorMessage);
    SubmoduleAddOutcome AddSubmodule(Repo primary, SubmoduleAddRequest request);
    SubmoduleUpdateOutcome UpdateSubmodules(Repo primary, SubmoduleUpdateRequest request);
    SubmoduleDeinitOutcome DeinitSubmodule(Repo primary, string submodulePath, bool force);
    IReadOnlyList<SubmodulePointerChange> GetSubmodulePointerChanges(Repo repo, string commitSha);
    MergePreviewResult PreviewMerge(Repo repo, string sourceRef);
    MergeOutcome Merge(Repo repo, string sourceRef, MergeStrategy strategy);
    RebasePreviewResult PreviewRebase(Repo repo, string targetRef);
    RebaseOutcome Rebase(Repo repo, string targetRef, bool autostash);
    CherryPickOutcome CherryPick(Repo repo, string commitSha);
    RevertCommitOutcome RevertCommit(Repo repo, string commitSha);
    // Per-file conflict resolution. TakeOurs/TakeTheirs check out the chosen side and stage
    // it; MarkResolved stages the working-tree file as-is (manual-edit path). Each returns a
    // ResolveOutcome and broadcasting is left to the caller.
    ResolveOutcome TakeOurs(Repo repo, string path);
    ResolveOutcome TakeTheirs(Repo repo, string path);
    // Resolves by keeping both sides: writes ours' content followed by theirs' content and stages.
    ResolveOutcome TakeBoth(Repo repo, string path);
    ResolveOutcome MarkResolved(Repo repo, string path);
    // Ours/theirs/base blob text for a conflicted path (stages 2/3/1). Any side may be null
    // when that stage is absent (add/add has no base, delete/modify is missing a side).
    ConflictSides GetConflictSides(Repo repo, string path);
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

public sealed record MergeOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

public enum RebasePreviewState
{
    Clean,
    Conflicts,
    Unknown,
}

public sealed record RebasePreviewResult(RebasePreviewState State, string? ErrorMessage);

public sealed record RebaseOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

// HasConflicts mirrors the merge/rebase pattern: a cherry-pick that conflicts exits
// non-zero but leaves CHERRY_PICK_HEAD behind, which the operation banner picks up — so
// it's reported as a successful start that produced conflicts, not an error.
public sealed record CherryPickOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

// Same as CherryPickOutcome but for `git revert`, whose conflict sentinel is REVERT_HEAD.
public sealed record RevertCommitOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

// ForceQuitAvailable is set when the regular --abort failed but the in-progress state is
// recoverable via `git X --quit` or direct sentinel removal — i.e. the user can choose to
// give up on restoring HEAD and just clear the marker files. Surfaced to the dialog so it
// can flip its confirm button to a "Force clear" action on the second click.
public sealed record AbortOperationOutcome(bool Success, string? ErrorMessage, bool ForceQuitAvailable = false);

// HasMoreConflicts is set when `git X --continue` refused because the working tree still
// has unmerged paths — the operation banner stays up and we surface the message so the
// user knows they have files left to resolve and stage.
public sealed record ContinueOperationOutcome(bool Success, string? ErrorMessage, bool HasMoreConflicts = false);

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

public sealed record PushOutcome(bool Success, string? ErrorMessage);

public sealed record PullOutcome(bool Success, string? ErrorMessage);

public sealed record FetchOutcome(bool Success, string? ErrorMessage);

public sealed record FastForwardOutcome(bool Success, string? ErrorMessage);

public sealed record CheckoutOutcome(bool Success, string? ErrorMessage);

public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}

public sealed record ResetOutcome(bool Success, string? ErrorMessage);

public sealed record CreateBranchOutcome(bool Success, string? ErrorMessage);

public sealed record MoveBranchOutcome(bool Success, string? ErrorMessage);

public sealed record CreateTagOutcome(bool Success, string? ErrorMessage);

public sealed record DeleteTagOutcome(bool Success, string? ErrorMessage);

public sealed record RenameBranchOutcome(bool Success, string? ErrorMessage);

public sealed record DeleteBranchOutcome(bool Success, string? ErrorMessage);

public sealed record DeleteRemoteBranchOutcome(bool Success, string? ErrorMessage);

public sealed record EditRemoteOutcome(bool Success, string? ErrorMessage);

public sealed record StashOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

public sealed record ResolveOutcome(bool Success, string? ErrorMessage);

// Text of each conflict side for a path; null when that stage doesn't exist. ErrorMessage
// is set only on an outright failure (not a git repo, etc.), not for a merely-absent side.
public sealed record ConflictSides(string? Base, string? Ours, string? Theirs, string? ErrorMessage);

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
