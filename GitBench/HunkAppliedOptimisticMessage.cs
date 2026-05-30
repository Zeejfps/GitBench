namespace GitGui;

// Sent by DiffViewModel right before git apply runs so LocalChangesViewModel can update
// its file lists without waiting on the eventual `git status` reload. The reload still
// runs and reconciles; this message just paints the expected end state immediately.
public readonly record struct HunkAppliedOptimisticMessage(
    Guid RepoId,
    string Path,
    DiffSide FromSide,
    DiffSide? ToSide,
    bool IsLastHunk);
