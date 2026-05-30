namespace GitGui;

public sealed record LocalChangesSnapshot(
    Guid RepoId,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    string? ErrorMessage);
