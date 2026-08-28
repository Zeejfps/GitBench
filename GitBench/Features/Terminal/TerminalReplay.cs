using System.Text.RegularExpressions;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// A recorded pseudo-terminal session: the bytes a program wrote, and the geometry it wrote them
/// at.
/// </summary>
/// <remarks>
/// The size is loaded rather than assumed for the reason the corpus suite states: a stream recorded
/// at 120 columns and replayed at 80 wraps in different places and produces a different screen, so
/// bytes alone do not determine one. It is read from the inventory the probe harness writes beside
/// the recording, which is where the recorder already put it.
/// </remarks>
internal sealed partial record TerminalRecording(byte[] Bytes, TerminalSize Size)
{
    /// <summary>
    /// Loads the recording at <paramref name="path"/> and the geometry from its inventory sibling.
    /// </summary>
    /// <exception cref="FileNotFoundException">Either file is missing.</exception>
    /// <exception cref="InvalidDataException">The inventory does not say what size it was recorded at.</exception>
    public static TerminalRecording Load(string path)
    {
        var bytes = File.ReadAllBytes(path);

        var inventoryPath = Path.ChangeExtension(path, null) + ".inventory.txt";
        if (!File.Exists(inventoryPath))
            throw new FileNotFoundException(
                $"No inventory beside the recording at '{inventoryPath}', so the geometry it was "
                + "recorded at is unknown and replaying it would show a screen that never happened.",
                inventoryPath);

        var match = TerminalLine().Match(File.ReadAllText(inventoryPath));
        if (!match.Success)
            throw new InvalidDataException(
                $"'{inventoryPath}' has no 'terminal: COLSxROWS' line.");

        return new TerminalRecording(
            bytes,
            new TerminalSize(
                int.Parse(match.Groups["cols"].Value),
                int.Parse(match.Groups["rows"].Value)));
    }

    [GeneratedRegex(@"^terminal:\s*(?<cols>\d+)x(?<rows>\d+)", RegexOptions.Multiline)]
    private static partial Regex TerminalLine();
}

/// <summary>
/// Replays a recording into the pane instead of spawning a shell, so the renderer can be looked at
/// without a working pseudo-terminal, a working spawn, or a program that behaves the same twice.
/// </summary>
/// <remarks>
/// Pinned to the size it was recorded at, so what appears is the screen the recording actually
/// produced and can be compared against the suite's golden for the same corpus. Resizing the pane
/// therefore does nothing, which is the point.
/// </remarks>
internal sealed class ReplayLaunch : ITerminalLaunch
{
    readonly TerminalRecording _recording;
    readonly ITerminalEngineFactory _engines;

    public ReplayLaunch(TerminalRecording recording, ITerminalEngineFactory engines)
    {
        _recording = recording;
        _engines = engines;
    }

    public TerminalSize SizeFor(TerminalSize viewport) => _recording.Size;

    public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
        TerminalSession.Start(
            () => new RecordedPtySession(_recording.Bytes),
            _engines,
            _recording.Size,
            dispatcher);
}

/// <summary>
/// A pseudo-terminal whose output is a fixed array of bytes and whose input goes nowhere.
/// </summary>
/// <remarks>
/// Ends its stream once the recording runs out, exactly as a real one does when its child's output
/// is finished — so the reader thread retires on its own and the last screen stays put.
/// </remarks>
internal sealed class RecordedPtySession : IPtySession
{
    readonly byte[] _bytes;
    readonly Lock _gate = new();
    readonly TaskCompletionSource<PtyExit> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _offset;
    bool _disposed;

    public RecordedPtySession(byte[] bytes) => _bytes = bytes;

    public Task<PtyExit> Exited => _exited.Task;

    public int ReadOutput(Span<byte> buffer)
    {
        lock (_gate)
        {
            if (_disposed) return 0;

            var remaining = _bytes.Length - _offset;
            if (remaining <= 0)
            {
                _exited.TrySetResult(new PtyExit.Completed(0));
                return 0;
            }

            var take = Math.Min(remaining, buffer.Length);
            _bytes.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }
    }

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Resize(PtySize size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _exited.TrySetResult(new PtyExit.TornDown());
    }
}
