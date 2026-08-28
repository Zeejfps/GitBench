namespace GitBench.Pty;

/// <summary>
/// Starts pseudo-terminal sessions.
/// </summary>
public interface IPtySessionFactory
{
    /// <summary>Spawns the configured program on a new pseudo-terminal.</summary>
    /// <exception cref="ArgumentException">The options describe something no host could start.</exception>
    /// <exception cref="PtySpawnException">The machine could not start it.</exception>
    /// <exception cref="PlatformNotSupportedException">The host has no pseudo-terminal support.</exception>
    IPtySession Start(PtySessionOptions options);
}
