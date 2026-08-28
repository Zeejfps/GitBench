namespace GitBench.Pty;

/// <summary>
/// A child process running on a pseudo-terminal: bytes out, bytes in, a size, and an end.
/// </summary>
/// <remarks>
/// <para>
/// Signals are not modelled. The kernel line discipline (and ConPTY on Windows) turns control
/// characters into signals for the foreground process group, so a Ctrl-C is a byte like any other.
/// </para>
/// <para>
/// The output side is a blocking pull rather than a <see cref="Stream"/>. A terminal is read by one
/// dedicated thread handing batches to the UI, which is the whole of what a consumer does, and the
/// rest of the stream contract — seeking, length, position, flushing — describes nothing a
/// pseudo-terminal has.
/// </para>
/// </remarks>
public interface IPtySession : IDisposable
{
    /// <summary>
    /// Reads what the child has written to its terminal, blocking until at least one byte is
    /// available, and returning 0 once the stream has ended.
    /// </summary>
    /// <remarks>
    /// End of stream is the session's terminal event, and it arrives at or after <see cref="Exited"/>
    /// rather than with it: a child exiting does not by itself close the terminal, so bytes can still
    /// arrive after <see cref="Exited"/> has completed. A reader that stops at <see cref="Exited"/>
    /// truncates the session's last output. <see cref="IDisposable.Dispose"/> ends the stream too, so
    /// a blocked reader is released rather than stranded, and reading a session that has ended keeps
    /// returning 0 instead of throwing — a finished terminal is a normal outcome, not misuse.
    /// </remarks>
    int ReadOutput(Span<byte> buffer);

    /// <summary>Delivers bytes to the child as terminal input.</summary>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    void WriteInput(ReadOnlySpan<byte> bytes);

    /// <summary>Resizes the terminal, notifying the child the way the platform does (SIGWINCH on Unix).</summary>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    void Resize(PtySize size);

    /// <summary>
    /// Completes once the child is gone, saying whether it finished on its own or the session ended
    /// it. Completing does not mean the output has ended — see <see cref="ReadOutput"/>.
    /// </summary>
    Task<PtyExit> Exited { get; }
}
