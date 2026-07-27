namespace GitBench.Messages;

// Broadcast right after a commit lands so the toolbar's push count (and the status bar / repo row
// reading the same slot) snaps to ahead + 1 immediately instead of waiting on the post-commit
// reload: on the active repo the ahead count rides in on the file-list read's `git status`, which
// is the whole working-tree walk, so the push button used to sit disabled for a beat after the
// panel had already emptied. RepoStatusStore applies the patch and the reload confirms it.
//
// Only a plain commit sends this. An amend's effect on ahead isn't knowable without reading —
// amending a commit the upstream already has makes the branch diverge rather than grow — so that
// case waits for the reload, as does a branch with no upstream (nothing to be ahead of).
public readonly record struct LocalCommitOptimisticMessage(Guid RepoId);
