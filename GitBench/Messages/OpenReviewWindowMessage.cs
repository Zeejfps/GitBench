namespace GitBench.Messages;

// Broadcast from the branch context menu to open a dedicated Review window for a branch's
// range of commits. Carries the pinned session (repo + head ref, optional explicit base) —
// not live state — so the review window loads independently and stays locked to that range
// regardless of the main window's active repo or selection. Mirrors OpenDiffWindowMessage.
// A null BaseRef means "auto" (resolve via merge-base); resolution lands in a later phase.
// Handled by ReviewWindowsViewModel.
public readonly record struct OpenReviewWindowMessage(
    Guid RepoId,
    string HeadRef,
    string HeadLabel,
    string? BaseRef = null,
    string? BaseLabel = null);
