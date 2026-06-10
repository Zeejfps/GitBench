using ZGF.Observable;

namespace GitBench;

public interface IRepoRegistry
{
    ObservableList<Repo> Repos { get; }
    ObservableList<Group> Groups { get; }
    State<Repo?> Active { get; }
    State<Guid?> RenamingGroupId { get; }
    // Bumped whenever the set of children (worktrees OR submodules) attached to a primary
    // changes, or when expand state flips. Single counter so the chevron/RepoEntry need
    // only watch one slot to know "something nested moved."
    State<int> WorktreesChanged { get; }
    void Open(string path);
    void SetActive(Guid id);
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
    // Shared expand toggle: a single chevron on the primary controls visibility of both
    // worktree and submodule children. Naming kept for backward compatibility.
    bool IsWorktreeExpanded(Guid primaryId);
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
