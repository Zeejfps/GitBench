using GitBench.Infrastructure;

namespace GitBench.Git;

public abstract record GitOutcome : IOutcome<GitOutcome>
{
    private GitOutcome() { }

    public static readonly GitOutcome Ok = new Success();

    public static GitOutcome Fail(string message) => new Failed(message);

    public sealed record Success : GitOutcome;

    public sealed record Failed(string Message) : GitOutcome;
}

// Operations that can land in a conflicted-but-in-progress state the operation banner
// takes over from: merge, rebase, cherry-pick, revert, stash apply, submodule update.
public abstract record MergeLikeOutcome : IOutcome<MergeLikeOutcome>
{
    private MergeLikeOutcome() { }

    public static readonly MergeLikeOutcome Ok = new Completed();

    public static MergeLikeOutcome Fail(string message) => new Failed(message);

    public sealed record Completed : MergeLikeOutcome;

    public sealed record Conflicted : MergeLikeOutcome;

    public sealed record Failed(string Message) : MergeLikeOutcome;
}

public abstract record PullOutcome : IOutcome<PullOutcome>
{
    private PullOutcome() { }

    public static readonly PullOutcome Ok = new Completed();

    public static PullOutcome Fail(string message) => new Failed(message);

    public sealed record Completed : PullOutcome;

    // Local and upstream both moved and git refused to pick merge-vs-rebase on its own.
    // The Pull button catches this and reruns with an explicit PullStrategy.
    public sealed record Diverged : PullOutcome;

    public sealed record Failed(string Message) : PullOutcome;
}

public abstract record AbortOutcome : IOutcome<AbortOutcome>
{
    private AbortOutcome() { }

    public static readonly AbortOutcome Ok = new Completed();

    public static AbortOutcome Fail(string message) => new Failed(message);

    public sealed record Completed : AbortOutcome;

    // ForceQuitAvailable: the regular --abort failed but the in-progress state is
    // recoverable via `git X --quit` or direct sentinel removal — the dialog flips its
    // confirm button to "Force clear" on the second click.
    public sealed record Failed(string Message, bool ForceQuitAvailable = false) : AbortOutcome;
}

public abstract record ContinueOutcome : IOutcome<ContinueOutcome>
{
    private ContinueOutcome() { }

    public static readonly ContinueOutcome Ok = new Completed();

    public static ContinueOutcome Fail(string message) => new Failed(message);

    public sealed record Completed : ContinueOutcome;

    // `git X --continue` refused because the working tree still has unmerged paths —
    // the banner stays up and tells the user they have files left to resolve.
    public sealed record MoreConflicts(string Message) : ContinueOutcome;

    public sealed record Failed(string Message) : ContinueOutcome;
}

public abstract record CloneOutcome : IOutcome<CloneOutcome>
{
    private CloneOutcome() { }

    public static CloneOutcome Fail(string message) => new Failed(message);

    public sealed record Cloned(string RepoPath) : CloneOutcome;

    public sealed record Failed(string Message) : CloneOutcome;
}
