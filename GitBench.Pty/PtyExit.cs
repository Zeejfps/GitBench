namespace GitBench.Pty;

/// <summary>
/// How a pseudo-terminal session ended.
/// </summary>
/// <remarks>
/// A killed child's exit code is whatever the platform stamped on it during teardown, so a bare
/// integer would report a number that means nothing. The two cases are kept apart instead, because
/// they are the two the caller acts on differently: a shell that exited on its own is worth offering
/// to restart, and one we ended is not. The hierarchy is closed — the private constructor means
/// these are the only two cases — but C# checks no switch for exhaustiveness, so every switch over
/// it needs a default arm that throws.
/// </remarks>
public abstract record PtyExit
{
    PtyExit()
    {
    }

    /// <summary>The child ran to completion on its own, leaving <paramref name="Code"/> behind.</summary>
    public sealed record Completed(int Code) : PtyExit;

    /// <summary>The session was disposed while the child was still running, so the session ended it.</summary>
    public sealed record TornDown : PtyExit;
}
