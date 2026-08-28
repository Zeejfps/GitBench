namespace GitBench.Pty;

/// <summary>
/// Why a pseudo-terminal session could not be started.
/// </summary>
public enum PtySpawnFailure
{
    /// <summary>The executable could not be found, on PATH or at the path given.</summary>
    ExecutableNotFound,

    /// <summary>The working directory does not exist.</summary>
    WorkingDirectoryNotFound,

    /// <summary>The host refused to start the program.</summary>
    AccessDenied,

    /// <summary>Anything else the platform reported.</summary>
    Other,
}

/// <summary>
/// A pseudo-terminal session could not be started because of the state of the machine it was asked
/// to start on — a shell that is not installed, a repository directory that has been deleted, a host
/// that refused.
/// </summary>
/// <remarks>
/// Separate from the <see cref="ArgumentException"/> the factory throws for options no host could
/// ever satisfy: that is a bug, this is the user's to fix and therefore the one worth catching and
/// showing. One type rather than a platform-shaped set of them, so that a caller does not end up
/// catching <see cref="Exception"/> to cover Win32 and errno both.
/// </remarks>
public sealed class PtySpawnException : Exception
{
    public PtySpawnException(
        PtySpawnFailure failure,
        string executable,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        Executable = executable;
    }

    public PtySpawnFailure Failure { get; }

    /// <summary>The program the session tried to start.</summary>
    public string Executable { get; }
}
