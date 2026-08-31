namespace GitBench.Git;

// The whole of git, for the few callers that genuinely span capabilities. Prefer depending on the
// narrowest capability interface a type actually uses — that is what keeps its test doubles small.
public interface IGitService :
    IGitRepositoryReader,
    IGitStatusReader,
    IGitDiffReader,
    IGitHistoryReader,
    IGitBranchOperations,
    IGitWorkingTreeOperations,
    IGitStashOperations,
    IGitRemoteOperations,
    IGitTagOperations,
    IGitIntegrationOperations,
    IGitConflictOperations,
    IGitWorktreeOperations,
    IGitSubmoduleOperations,
    IGitConfigOperations,
    IGitRepositoryLifecycle
{
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

// Cheap per-repo signals composed from a `git status --porcelain=v2 --branch`: the current
// branch / detached / upstream + ahead/behind (the branch header) and whether the working tree has
// any changes (any non-header record). Powers the RepoBar dirty dot, the toolbar's push/pull
// availability, and the status bar. Two reads fill it: GetStatusSummary's all-repos probe, and the
// active/warm repo's file-list read (GetLocalChanges), which parses the same headers in one pass so
// the file lists and the summary always describe one observation.
public sealed record GitStatusSummary(
    string? Branch,
    bool IsDetached,
    bool HasUpstream,
    int Ahead,
    int Behind,
    bool IsDirty)
{
    public static readonly GitStatusSummary Unknown = new(null, false, false, 0, 0, false);

    // Replaces the sync half with a fresher refs-only observation, keeping the dirty flag this
    // summary already carries — a GetSyncSummary read never looked at the working tree.
    public GitStatusSummary With(GitSyncSummary sync) =>
        new(sync.Branch, sync.IsDetached, sync.HasUpstream, sync.Ahead, sync.Behind, IsDirty);
}

// The sync half of GitStatusSummary — where HEAD is and how it stands against its upstream —
// answered entirely out of `.git`. This is what a fetch actually changes, and reading it costs a
// HEAD lookup plus an ahead/behind count bounded by the divergence, rather than the whole-worktree
// walk GetStatusSummary pays to also learn whether the tree is dirty.
public sealed record GitSyncSummary(
    string? Branch,
    bool IsDetached,
    bool HasUpstream,
    int Ahead,
    int Behind)
{
    public static readonly GitSyncSummary Unknown = new(null, false, false, 0, 0);
    public static readonly GitSyncSummary Detached = new(null, true, false, 0, 0);
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

// One unmerged path and what each side did to it, derived from which of git's merge stages
// (1=base, 2=ours, 3=theirs) the index still holds for it.
public sealed record ConflictedPath(string Path, ConflictChangeKind Ours, ConflictChangeKind Theirs);

// The three sides of one unmerged path as text, straight from the index. A null side is one that
// does not exist: no common ancestor for an add/add, or the side that deleted the file — which is
// a different fact from that side being empty.
public sealed record ConflictStages(string? Base, string? Ours, string? Theirs);
