using GitBench.Features.Commits;

namespace GitBench.Features.LocalChanges;

public sealed record LocalChangesSnapshot(
    Guid RepoId,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged);
