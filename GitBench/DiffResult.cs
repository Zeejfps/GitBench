namespace GitGui;

public enum DiffSide { Unstaged, Staged, Commit }

public enum DiffLineKind { Context, Added, Removed }

public sealed record DiffLine(
    DiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Text);

public sealed record DiffHunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    string? Header,
    IReadOnlyList<DiffLine> Lines);

public sealed record DiffResult(
    Guid RepoId,
    string Path,
    string? OldPath,
    DiffSide Side,
    bool IsBinary,
    bool IsModeOnly,
    int? OldMode,
    int? NewMode,
    IReadOnlyList<DiffHunk> Hunks,
    bool Truncated,
    string? ErrorMessage);
