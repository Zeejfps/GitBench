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
    PushOutcome Push(Repo repo, bool force = false);
    PushOutcome PublishBranch(Repo repo, string localBranch, string remoteName, string remoteBranchName, bool setUpstream);
    IReadOnlyList<string> GetRemoteNames(Repo repo);
    string? GetRemoteUrl(Repo repo, string remoteName);
    EditRemoteOutcome EditRemote(Repo repo, string oldName, string newName, string url);
    PullOutcome Pull(Repo repo);
    FetchOutcome Fetch(Repo repo);
    FastForwardOutcome FastForwardBranch(Repo repo, string localBranch, string remoteName, string remoteBranch, Action<string>? onLine = null);
    CheckoutOutcome CheckoutLocalBranch(Repo repo, string branchName);
    CheckoutOutcome CheckoutRemoteBranch(Repo repo, string localName, string remoteName, string remoteBranchName, bool track);
    ResetOutcome ResetCurrent(Repo repo, string commitSha, ResetMode mode);
    CreateBranchOutcome CreateBranch(Repo repo, string name, string startPoint, bool checkout);
    RenameBranchOutcome RenameBranch(Repo repo, string oldName, string newName, bool force);
    DeleteBranchOutcome DeleteBranch(Repo repo, string name, bool force);
    DeleteRemoteBranchOutcome DeleteRemoteBranch(Repo repo, string remoteName, string branchName);
    StashOutcome CreateStash(Repo repo, string message, bool includeUntracked, bool keepIndex, IReadOnlyList<string> paths);
    StashOutcome ApplyStash(Repo repo, int index);
    StashOutcome DropStash(Repo repo, int index);
    StashOutcome RenameStash(Repo repo, int index, string newMessage);
    DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null);
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

public sealed record RenameBranchOutcome(bool Success, string? ErrorMessage);

public sealed record DeleteBranchOutcome(bool Success, string? ErrorMessage);

public sealed record DeleteRemoteBranchOutcome(bool Success, string? ErrorMessage);

public sealed record EditRemoteOutcome(bool Success, string? ErrorMessage);

public sealed record StashOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);
