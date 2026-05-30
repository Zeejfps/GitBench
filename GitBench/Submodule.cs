namespace GitGui;

public enum SubmoduleStatus
{
    // Parent's recorded pointer matches the submodule's currently checked-out commit.
    UpToDate,
    // Submodule's HEAD differs from the parent's recorded pointer (commits ahead/behind/diverged).
    Modified,
    // Submodule is listed in .gitmodules but has no working tree / .git dir locally yet.
    NotInitialized,
    // Submodule has unresolved merge conflicts in its index.
    MergeConflict,
}

public sealed record SubmoduleInfo(
    // Path relative to the parent repo's working tree.
    string Path,
    // Absolute path on disk.
    string AbsolutePath,
    // URL recorded in .gitmodules (or null if the entry is malformed).
    string? Url,
    // Branch tracked via `submodule.<name>.branch` (or null when pinned to a SHA).
    string? Branch,
    // The SHA the parent's tree records for this submodule. May be null if it can't
    // be read (e.g. the parent isn't checked out cleanly).
    string? RecordedSha,
    // The SHA actually checked out in the submodule. Null if NotInitialized.
    string? CurrentSha,
    SubmoduleStatus Status,
    // `git describe` output for the current SHA if available — used in tooltips.
    string? Describe);

// Per-commit submodule pointer change extracted from `git diff-tree`. Used to render a
// single row in CommitDetailsView showing how the parent moved a submodule pointer.
// FromSha == 40-zero SHA when the submodule was added in this commit; ToSha == 40-zero
// when it was removed.
public sealed record SubmodulePointerChange(
    string Path,
    string FromSha,
    string ToSha,
    int AheadCount,
    int BehindCount,
    string? ShortLog);

public sealed record SubmoduleAddRequest(
    string Url,
    // Path inside the parent's working tree (relative or absolute).
    string Path,
    // Optional branch to track via `submodule.<name>.branch`. Null = pin to current commit.
    string? Branch,
    // Force adds when the path already exists or has been used before.
    bool Force);

public sealed record SubmoduleAddOutcome(bool Success, string? ErrorMessage);

public enum SubmoduleUpdateMode
{
    Checkout, // default: fast-forward / detach
    Merge,
    Rebase,
}

public sealed record SubmoduleUpdateRequest(
    // null = update every submodule under the parent. Otherwise restrict to the given paths.
    IReadOnlyList<string>? Paths,
    // Required when a submodule has never been initialized (no working tree yet).
    bool Init,
    bool Recursive,
    SubmoduleUpdateMode Mode);

public sealed record SubmoduleUpdateOutcome(bool Success, string? ErrorMessage, bool HasConflicts = false);

public sealed record SubmoduleDeinitOutcome(bool Success, string? ErrorMessage);
