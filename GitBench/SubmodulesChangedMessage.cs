namespace GitGui;

// Fired when the set of submodules attached to a primary repo may have changed —
// either through an in-app dialog (Add/Deinit/Update) or by the user editing
// `.gitmodules` or running `git submodule …` in a terminal. The SubmoduleSyncService
// should re-run discovery and reconcile the registry.
public readonly record struct SubmodulesChangedMessage(Guid PrimaryRepoId);
