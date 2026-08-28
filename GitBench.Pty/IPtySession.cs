namespace GitBench.Pty;

/// <summary>
/// A child process running on a pseudo-terminal: bytes out, bytes in, a size, and an exit.
/// </summary>
/// <remarks>
/// Signals are not modelled. The kernel line discipline (and ConPTY on Windows) turns control
/// characters into signals for the foreground process group, so a Ctrl-C is a byte like any other.
/// </remarks>
public interface IPtySession : IDisposable
{
    /// <summary>
    /// Everything the child writes to its terminal. Reads block until bytes arrive, and reach
    /// end-of-stream once the child has exited and the buffer is drained.
    /// </summary>
    Stream Output { get; }

    /// <summary>Delivers bytes to the child as terminal input.</summary>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Resizes the terminal, notifying the child the way the platform does (SIGWINCH on Unix).</summary>
    void Resize(PtySize size);

    /// <summary>Completes with the child's exit code once it terminates.</summary>
    Task<int> Exited { get; }
}
