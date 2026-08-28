using System.Runtime.Versioning;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// A pseudo-terminal session backed by the Windows console pseudo-console (ConPTY).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtySession : IPtySession
{
    public ConPtySession(PtySessionOptions options)
    {
        _ = options;
        throw new NotImplementedException("ConPTY session is not implemented yet.");
    }

    public Task<PtyExit> Exited => throw new NotImplementedException();

    public int ReadOutput(Span<byte> buffer) => throw new NotImplementedException();

    public void WriteInput(ReadOnlySpan<byte> bytes) => throw new NotImplementedException();

    public void Resize(PtySize size) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
