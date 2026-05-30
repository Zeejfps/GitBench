namespace GitGui;

// Broadcast after a successful push/fetch — views that depend on remote-tracking
// state (BranchesView's ahead/behind, ActionsToolbar's push availability) refetch.
public readonly record struct RefsChangedMessage(Guid RepoId);
