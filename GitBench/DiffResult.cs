namespace GitBench;

public enum DiffSide { Unstaged, Staged, Commit }

public enum DiffLineKind { Context, Added, Removed }

public sealed record DiffLine(
    DiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Text)
{
    // Set when the unified diff emitted "\ No newline at end of file" for this line — i.e.
    // this line is the last on its side and that side has no trailing newline. Preserved so
    // HunkPatchBuilder can round-trip it; dropping it makes a staged/discarded hunk silently
    // gain a trailing newline, which git apply then writes into the working tree.
    public bool NoNewlineAtEof { get; init; }
}

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
    string? ErrorMessage,
    // Whether the file is tracked by Git LFS (per .gitattributes). Only meaningful for
    // binary files — text diffs leave this false and the UI shows no LFS badge for them.
    bool IsLfs = false);
