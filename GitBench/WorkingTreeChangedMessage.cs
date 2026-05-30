namespace GitGui;

// Broadcast when the working tree or index has changed in a way that affects
// `git status` output — either a user edit picked up by the filesystem watcher
// or an in-app stage/unstage. Distinct from RefsChangedMessage so a save in the
// editor doesn't repaint the commit graph or branches; only LocalChanges reloads.
public readonly record struct WorkingTreeChangedMessage(Guid RepoId);
