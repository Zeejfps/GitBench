using System.Runtime.Versioning;

namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// A pseudo-terminal session backed by openpty and a session-leading posix_spawn.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal sealed class UnixPtySession : IPtySession
{
    public UnixPtySession(PtySessionOptions options)
    {
        _ = options;
        throw new NotImplementedException("Unix pseudo-terminal session is not implemented yet.");
    }

    public Task<PtyExit> Exited => throw new NotImplementedException();

    public int ReadOutput(Span<byte> buffer) => throw new NotImplementedException();

    public void WriteInput(ReadOnlySpan<byte> bytes) => throw new NotImplementedException();

    public void Resize(PtySize size) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
