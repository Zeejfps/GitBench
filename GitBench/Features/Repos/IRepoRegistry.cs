using GitBench.Features.Branches;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// The result of trying to open a folder as a repo: added fresh, already present (and re-activated),
// or rejected because the folder has no git working tree. Lets the caller surface the rejection.
public enum OpenRepoOutcome
{
    Opened,
    AlreadyOpen,
    NotAGitRepo,
}

public interface IRepoRegistry
{
    ObservableList<Repo> Repos { get; }
    ObservableList<Group> Groups { get; }
    State<Repo?> Active { get; }
    State<Guid?> RenamingGroupId { get; }
    // Bumped whenever the set of children (worktrees OR submodules) attached to a primary
    // changes, or a primary's branch is recorded. Watched by BranchesViewModel to refresh the
    // markers for branches a sibling worktree has checked out. (Row expand/collapse is its own
    // per-row observable — see WatchWorktreeExpanded — and no longer rides this counter.)
    State<int> WorktreesChanged { get; }
    // Bumped whenever a repo's keyboard hotkey slot is assigned or cleared, so a row's badge
    // re-derives its digit. The slot map itself is read through RepoForHotkey / HotkeyFor.
    State<int> HotkeysChanged { get; }
    OpenRepoOutcome Open(string path, Guid? groupId = null);
    void SetActive(Guid id);
    // The primary repo bound to a 1-9 hotkey slot, or null if the slot is unassigned.
    Guid? RepoForHotkey(int slot);
    // The slot (1-9) a repo currently occupies, or null if it has none.
    int? HotkeyFor(Guid repoId);
    // Pins a primary repo to a slot. A repo holds at most one slot and a slot points at one repo,
    // so this drops the repo's previous slot and steals the slot from any prior holder.
    void AssignHotkey(Guid repoId, int slot);
    void ClearHotkey(int slot);
    void ToggleGroupCollapsed(Guid groupId);
    // Collapses or expands every group at once (RepoBar's "Collapse All" / "Expand All").
    void SetAllGroupsCollapsed(bool collapsed);
    Guid CreateGroup(string name);
    void RenameGroup(Guid id, string newName);
    void DeleteGroup(Guid id);
    void MoveRepo(Guid repoId, Guid targetGroupId, int insertIndex);
    void MoveGroup(Guid groupId, int insertIndex);
    void RemoveRepo(Guid repoId);
    void BeginRenameGroup(Guid id);
    void EndRenameGroup();
    BranchesUiState GetBranchesUi(Guid repoId);
    void SetBranchesUi(Guid repoId, BranchesUiState state);
    IEnumerable<Repo> GetWorktrees(Guid primaryId);
    IEnumerable<Repo> GetSubmodules(Guid primaryId);
    // Shared fold for a row's children (worktrees AND submodules). A live observable so the
    // chevron glyph and the child-row lists bind to it directly; the name predates submodules.
    IReadable<bool> WatchWorktreeExpanded(Guid primaryId);
    void SetWorktreeExpanded(Guid primaryId, bool expanded);
    // Manual per-repo identity override (a profile id) that takes precedence over auto-matching.
    Guid? GetIdentityOverride(Guid repoId);
    void SetIdentityOverride(Guid repoId, Guid? profileId);
    Guid? GetIdentityOverrideByPath(string path);
    void ReplaceWorktreesFor(Guid primaryId, IReadOnlyList<WorktreeDescriptor> desired);
    // Reconciles the whole submodule tree under a primary in one pass: each node carries its
    // own nested children, so submodules-of-submodules are added/updated/removed recursively.
    void ReplaceSubmoduleForest(Guid primaryId, IReadOnlyList<SubmoduleNode> roots);
    void SetPrimaryBranch(Guid primaryId, string? branch);
}
