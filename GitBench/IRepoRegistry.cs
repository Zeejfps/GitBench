using ZGF.Observable;

namespace GitGui;

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
    void ReplaceWorktreesFor(Guid primaryId, IReadOnlyList<WorktreeDescriptor> desired);
    void ReplaceSubmodulesFor(Guid primaryId, IReadOnlyList<SubmoduleDescriptor> desired);
    void SetPrimaryBranch(Guid primaryId, string? branch);
}
