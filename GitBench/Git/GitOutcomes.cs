using GitBench.Infrastructure;

namespace GitBench.Git;

public abstract record GitOutcome : IOutcome<GitOutcome>
{
    private GitOutcome() { }

    public static readonly GitOutcome Ok = new Success();

    public static GitOutcome Fail(string message) => new Failed(message);

    public string? FailureMessage => (this as Failed)?.Message;

    public sealed record Success : GitOutcome;

    public sealed record Failed(string Message) : GitOutcome;
}

// Removing a worktree is two things at once: deregistering it, and deleting its directory. Git
// exits 1 for both "refused, nothing happened" and "deregistered it but couldn't finish the
// delete", so the outcome has to say which — the second one is not a failure, since the worktree
// the user asked to remove is gone either way.
public abstract record WorktreeRemoveOutcome : IOutcome<WorktreeRemoveOutcome>
{
    private WorktreeRemoveOutcome() { }

    public static readonly WorktreeRemoveOutcome Ok = new Removed();

    public static WorktreeRemoveOutcome Fail(string message) => new Failed(message);

    // RemovedWithLeftovers is not a failure: the worktree is deregistered and its directory is
    // all that's left. Callers surface it as a warning and still refresh the worktree list.
    public string? FailureMessage => (this as Failed)?.Message;

    public sealed record Removed : WorktreeRemoveOutcome;

    /// <param name="Path">The worktree directory that still exists on disk.</param>
    /// <param name="Reason">Why the last entry under it couldn't be deleted.</param>
    public sealed record RemovedWithLeftovers(string Path, string Reason) : WorktreeRemoveOutcome;

    public sealed record Failed(string Message) : WorktreeRemoveOutcome;
}

// Operations that can land in a conflicted-but-in-progress state the operation banner
// takes over from: merge, rebase, cherry-pick, revert, stash apply, submodule update.
public abstract record MergeLikeOutcome : IOutcome<MergeLikeOutcome>
{
    private MergeLikeOutcome() { }

    public static readonly MergeLikeOutcome Ok = new Completed();

    public static MergeLikeOutcome Fail(string message) => new Failed(message);

    // Conflicted is not a failure: the operation landed and the banner takes over.
    public string? FailureMessage => (this as Failed)?.Message;

    public sealed record Completed : MergeLikeOutcome;

    public sealed record Conflicted : MergeLikeOutcome;

    public sealed record Failed(string Message) : MergeLikeOutcome;
}

public abstract record PullOutcome : IOutcome<PullOutcome>
{
    private PullOutcome() { }

    public static readonly PullOutcome Ok = new Completed();

    public static PullOutcome Fail(string message) => new Failed(message);

    public string? FailureMessage => (this as Failed)?.Message;

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

    public string? FailureMessage => (this as Failed)?.Message;

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

    public string? FailureMessage => this switch
    {
        Failed failed => failed.Message,
        MoreConflicts more => more.Message,
        _ => null,
    };

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

    public string? FailureMessage => (this as Failed)?.Message;

    // Warning carries git's complaint when the clone landed a usable repo but git still exited
    // non-zero — a failing post-checkout hook is folded into `git clone`'s own exit status, so the
    // working tree is fine and the hook is what needs reporting.
    public sealed record Cloned(string RepoPath, string? Warning = null) : CloneOutcome;

    public sealed record Failed(string Message) : CloneOutcome;
}
