namespace GitBench.Git;

public abstract record GitOutcome
{
    private GitOutcome() { }

    public static readonly GitOutcome Ok = new Success();

    public sealed record Success : GitOutcome;

    public sealed record Failed(string Message) : GitOutcome;

    // outcome is null only when the background runner itself failed (thread-level error).
    public static GitOutcome Normalize(GitOutcome? outcome, string? error)
        => outcome ?? new Failed(error ?? "Operation failed.");
}

// Operations that can land in a conflicted-but-in-progress state the operation banner
// takes over from: merge, rebase, cherry-pick, revert, stash apply, submodule update.
public abstract record MergeLikeOutcome
{
    private MergeLikeOutcome() { }

    public static readonly MergeLikeOutcome Ok = new Completed();

    public sealed record Completed : MergeLikeOutcome;

    public sealed record Conflicted : MergeLikeOutcome;

    public sealed record Failed(string Message) : MergeLikeOutcome;

    public static MergeLikeOutcome Normalize(MergeLikeOutcome? outcome, string? error)
        => outcome ?? new Failed(error ?? "Operation failed.");
}

public abstract record PullOutcome
{
    private PullOutcome() { }

    public static readonly PullOutcome Ok = new Completed();

    public sealed record Completed : PullOutcome;

    // Local and upstream both moved and git refused to pick merge-vs-rebase on its own.
    // The Pull button catches this and reruns with an explicit PullStrategy.
    public sealed record Diverged : PullOutcome;

    public sealed record Failed(string Message) : PullOutcome;
}

public abstract record AbortOutcome
{
    private AbortOutcome() { }

    public static readonly AbortOutcome Ok = new Completed();

    public sealed record Completed : AbortOutcome;

    // ForceQuitAvailable: the regular --abort failed but the in-progress state is
    // recoverable via `git X --quit` or direct sentinel removal — the dialog flips its
    // confirm button to "Force clear" on the second click.
    public sealed record Failed(string Message, bool ForceQuitAvailable = false) : AbortOutcome;
}

public abstract record ContinueOutcome
{
    private ContinueOutcome() { }

    public static readonly ContinueOutcome Ok = new Completed();

    public sealed record Completed : ContinueOutcome;

    // `git X --continue` refused because the working tree still has unmerged paths —
    // the banner stays up and tells the user they have files left to resolve.
    public sealed record MoreConflicts(string Message) : ContinueOutcome;

    public sealed record Failed(string Message) : ContinueOutcome;
}

public abstract record CloneOutcome
{
    private CloneOutcome() { }

    public sealed record Cloned(string RepoPath) : CloneOutcome;

    public sealed record Failed(string Message) : CloneOutcome;
}
