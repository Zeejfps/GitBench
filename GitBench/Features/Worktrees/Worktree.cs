namespace GitBench.Features.Worktrees;

public sealed record WorktreeInfo(
    string Path,
    string? HeadSha,
    string? Branch,
    bool IsDetached,
    bool IsBare,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason);

public sealed record WorktreeAddRequest(
    string Path,
    string StartPoint,
    string? NewBranchName,
    bool Force);



