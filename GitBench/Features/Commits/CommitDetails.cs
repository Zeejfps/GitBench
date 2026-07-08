using GitBench.Features.Submodules;

namespace GitBench.Features.Commits;

public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmodified,
    Conflicted,
    // Submodule pointer change. The accompanying PointerChange field carries the
    // from/to SHAs and (when the submodule is locally initialized) a shortlog.
    Submodule,
}

public sealed record FileChange(
    string Path,
    string? OldPath,
    FileChangeStatus Status)
{
    // Non-null only when Status == Submodule. Carries the SHA range that the parent
    // commit moved the submodule between, so the row can render "abc..def (+N commits)"
    // and a click can activate the submodule's own history at that range.
    public SubmodulePointerChange? PointerChange { get; init; }

    // The file's content identity on the after-side (its git blob OID at head). Populated for
    // combined-range loads so a review's Viewed mark can tell when a file's content changed since it
    // was marked; null for surfaces that don't carry it. Deletions carry the all-zero blank OID.
    public string? ContentId { get; init; }
}

public sealed record CommitDetails(
    Guid RepoId,
    string Sha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorWhen,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitterWhen,
    string Message,
    string MessageShort,
    IReadOnlyList<string> ParentShas,
    IReadOnlyList<FileChange> Files);
