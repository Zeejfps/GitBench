namespace GitBench;

public sealed record LocalChangesSnapshot(
    Guid RepoId,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    string? ErrorMessage,
    // Full multi-line git error block (stderr+stdout) behind a failed status read. The panel
    // shows ErrorMessage (one line) inline and offers this in a scrollable dialog on demand —
    // status recurses into submodules, so the real cause ("failed in submodule X", a "fatal:"
    // from the recursed child) often lives on lines past the first.
    string? ErrorDetail = null);
