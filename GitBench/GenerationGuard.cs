namespace GitGui;

/// <summary>
/// Generation token guard for invalidating outstanding async work. The view bumps the
/// generation when it kicks off (or aborts) a load and captures the new token; the
/// background continuation checks the token before applying its result, so any work
/// from a previous load is silently dropped.
/// </summary>
internal sealed class GenerationGuard
{
    private int _current;

    /// <summary>Bumps the generation and returns the new token to capture.</summary>
    public int Bump() => ++_current;

    /// <summary>The current token, captured without bumping — for cross-lane guards.</summary>
    public int Current => _current;

    /// <summary>True when <paramref name="token"/> is no longer the current generation.</summary>
    public bool IsStale(int token) => token != _current;
}
