namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// What an errno means to a session, as decisions rather than as numbers scattered through the
/// syscall loops.
/// </summary>
/// <remarks>
/// <para>
/// A table rather than inline conditions, because two of the answers here cannot be reached on a Mac
/// and one cannot be reached deliberately anywhere. Linux ends a terminal's output stream with EIO
/// where macOS returns 0, and a thread parked in a system call is interrupted on a schedule no test
/// can provoke — so the choices are either asserted here, on every host, or asserted nowhere.
/// </para>
/// <para>
/// The numbers this takes agree on macOS and Linux. EAGAIN does not — 35 there, 11 on Linux — which
/// is why nothing in this table turns on it.
/// </para>
/// </remarks>
internal static class UnixErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int E2BIG = 7;
    public const int EACCES = 13;
    public const int ENOTTY = 25;
    public const int EPIPE = 32;

    /// <summary>
    /// Whether a failed read on the master means the terminal is finished rather than broken.
    /// </summary>
    /// <remarks>
    /// The last close of the slave ends the stream, and Linux reports that end as EIO where macOS
    /// simply returns 0. Rethrowing it would turn the ordinary end of every session on one platform
    /// into an exception on a reader thread.
    /// </remarks>
    public static bool EndsTheOutputStream(int errno) => errno == EIO;

    /// <summary>Whether the call was interrupted and should simply be reissued.</summary>
    public static bool ShouldRetry(int errno) => errno == EINTR;

    /// <summary>
    /// Whether a failed write means the child is gone, which is dropped rather than reported.
    /// </summary>
    /// <remarks>
    /// Only the child's absence is swallowed. Everything else is a real failure and has to surface,
    /// because a write loop that treats every errno as a departed child turns a live terminal dead in
    /// the middle of a session and says nothing.
    /// </remarks>
    public static bool ChildIsGone(int errno) => errno is EIO or EPIPE;

    /// <summary>What a failed spawn is reported to the caller as.</summary>
    public static PtySpawnFailure ToSpawnFailure(int errno) => errno switch
    {
        ENOENT => PtySpawnFailure.ExecutableNotFound,
        EACCES or EPERM => PtySpawnFailure.AccessDenied,
        _ => PtySpawnFailure.Other,
    };
}
