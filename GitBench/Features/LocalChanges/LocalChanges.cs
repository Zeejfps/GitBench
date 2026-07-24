using GitBench.Features.Commits;
using GitBench.Git;

namespace GitBench.Features.LocalChanges;

public sealed record LocalChangesSnapshot(
    Guid RepoId,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    GitStatusSummary Summary)
{
    public static LocalChangesSnapshot Empty(Guid repoId) =>
        new(repoId, Array.Empty<FileChange>(), Array.Empty<FileChange>(), GitStatusSummary.Unknown);
}
