namespace GitBench.Messages;

// Broadcast when the working tree or index has changed in a way that affects
// `git status` output — either a user edit picked up by the filesystem watcher
// or an in-app stage/unstage. Distinct from RefsChangedMessage so a save in the
// editor doesn't repaint the commit graph or branches; only LocalChanges reloads.
//
// IndexOnly marks a mutation that moved content between HEAD and the index without
// touching a file on disk — a stage or an unstage. A HEAD→disk diff
// (DiffSide.WorkingTree) is invariant under those, so the working-tree review's
// stacked diffs skip a refetch that would return the same bytes; the staged and
// unstaged sides still reload, since their content is exactly what moved.
public readonly record struct WorkingTreeChangedMessage(Guid RepoId, bool IndexOnly = false);
