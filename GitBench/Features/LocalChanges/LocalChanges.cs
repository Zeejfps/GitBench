namespace GitBench;

public sealed record LocalChangesSnapshot(
    Guid RepoId,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    string? ErrorMessage,
    // Full multi-line git error block (stderr+stdout) behind a failed status read. The panel
    // shows ErrorMessage (one line) inline and offers this in a scrollable dialog on demand,
    // since the real cause (a trailing "fatal:"/"hint:" line) often sits past the first line.
    string? ErrorDetail = null);
