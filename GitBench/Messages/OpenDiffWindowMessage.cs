using GitBench.Features.Diff;

namespace GitBench.Messages;

// Broadcast by DiffViewModel when the user clicks "open in new window" in the diff header.
// Carries the pinned target (path/side/sha) — not a frozen result — so the popped-out
// window's own DiffViewModel loads and stays live for that file, independent of the main
// window's selection. RepoId carries the source pane's pinned repo (a Review-window commit
// diff); null ⇒ the pop-out follows the active repo, as Local Changes pop-outs do. Handled by
// DiffWindowPresenter.
public readonly record struct OpenDiffWindowMessage(DiffTarget Target, Guid? RepoId = null);
