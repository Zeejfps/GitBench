namespace GitGui;

// Fired when the set of worktrees attached to a primary repo may have changed —
// either through an in-app dialog or by the user running `git worktree add/remove/prune`
// in a terminal. The handler should re-run discovery and reconcile the registry.
public readonly record struct WorktreesChangedMessage(Guid PrimaryRepoId);
