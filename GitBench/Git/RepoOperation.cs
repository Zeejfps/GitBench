namespace GitBench.Git;

public interface IConflictableOperation
{
    bool HasConflicts { get; }
}

public interface ISequencerOperation : IConflictableOperation
{
    string? StoppedSubject { get; }
}

public interface IProgressOperation
{
    int Step { get; }
    int Total { get; }
}

public abstract record RepoOperation
{
    public abstract RepoOperationState Kind { get; }
}

public sealed record RebaseOperation(string? OntoLabel, int Step, int Total, string? StoppedSubject, bool HasConflicts)
    : RepoOperation, ISequencerOperation, IProgressOperation
{
    public override RepoOperationState Kind => RepoOperationState.Rebase;
}

public sealed record ApplyMailboxOperation(int Step, int Total, string? StoppedSubject, bool HasConflicts)
    : RepoOperation, ISequencerOperation, IProgressOperation
{
    public override RepoOperationState Kind => RepoOperationState.ApplyMailbox;
}

public sealed record CherryPickOperation(string? StoppedSubject, bool HasConflicts)
    : RepoOperation, ISequencerOperation
{
    public override RepoOperationState Kind => RepoOperationState.CherryPick;
}

public sealed record RevertOperation(string? StoppedSubject, bool HasConflicts)
    : RepoOperation, ISequencerOperation
{
    public override RepoOperationState Kind => RepoOperationState.Revert;
}

public sealed record MergeOperation(string? IncomingLabel, bool HasConflicts)
    : RepoOperation, IConflictableOperation
{
    public override RepoOperationState Kind => RepoOperationState.Merge;
}

public sealed record BisectOperation : RepoOperation
{
    public override RepoOperationState Kind => RepoOperationState.Bisect;
}

public sealed record UnmergedPathsOperation(bool HasConflicts)
    : RepoOperation, IConflictableOperation
{
    public override RepoOperationState Kind => RepoOperationState.UnmergedPaths;
}

public static class RepoOperationExtensions
{
    public static bool IsConflicted(this RepoOperation op) => op is IConflictableOperation c && c.HasConflicts;

    public static bool IsSequencer(this RepoOperation op) => op is ISequencerOperation;

    public static bool CanContinue(this RepoOperation op) => op is ISequencerOperation s && !s.HasConflicts;

    public static bool CanSkip(this RepoOperation op) => op is ISequencerOperation;

    public static string? StoppedSubject(this RepoOperation op) => (op as ISequencerOperation)?.StoppedSubject;

    public static bool ShowsCommitBox(this RepoOperation? op) => op is null or MergeOperation or UnmergedPathsOperation;
}
