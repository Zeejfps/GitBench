namespace GitGui;

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

public sealed record WorktreeAddOutcome(bool Success, string? ErrorMessage);

public sealed record WorktreeRemoveOutcome(bool Success, string? ErrorMessage);

public sealed record WorktreePruneOutcome(bool Success, string? ErrorMessage);
