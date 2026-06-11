namespace GitBench.Infrastructure;

/// <summary>
/// Generation token guard for invalidating outstanding async work. The view bumps the
/// generation when it kicks off (or aborts) a load and captures the new token; the
/// background continuation checks the token before applying its result, so any work
/// from a previous load is silently dropped.
/// </summary>
internal sealed class GenerationGuard
{
    private int _current;

    /// <summary>
    /// True while an exclusive op started via <c>TryRunBackground</c>/<c>TryRunOutcome</c>
    /// is in flight on this lane. UI-thread only — the runner sets it before dispatching
    /// and clears it in the posted continuation, replacing the per-VM boolean guards.
    /// </summary>
    public bool InFlight { get; internal set; }

    /// <summary>Bumps the generation and returns the new token to capture.</summary>
    public int Bump() => Interlocked.Increment(ref _current);

    /// <summary>The current token, captured without bumping — for cross-lane guards.</summary>
    public int Current => Volatile.Read(ref _current);

    /// <summary>True when <paramref name="token"/> is no longer the current generation.</summary>
    public bool IsStale(int token) => token != Volatile.Read(ref _current);
}
