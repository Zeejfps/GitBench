using System.Diagnostics;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// What happens when the shell produces faster than the UI thread parses. A terminal that buffers
/// without a bound does not merely fall behind, it diverges — so these pin the bound itself, and
/// that being bounded costs neither output nor a clean teardown.
/// </summary>
public class TerminalBackpressureTests
{
    static readonly TerminalSize Viewport = new(80, 24);

    [Fact]
    public void AFloodTheUiThreadNeverDrains_StopsBeingReadRatherThanBufferedForever()
    {
        var pty = new FloodPty();
        using var session = TerminalSession.Start(
            () => pty, new XtermSharpEngineFactory(), Viewport, new BlackHoleDispatcher());

        // The reader is never let go of a drain, so whatever it has taken is all it will ever take.
        Assert.True(
            pty.WaitUntilQuiet(TimeSpan.FromSeconds(5)),
            "the reader never stopped asking for output");

        // The bound is 256 KB; the reader may overshoot it by the one read already in its hands, and
        // the engine has one batch of its own. A megabyte is comfortably clear of that and just as
        // comfortably clear of unbounded, which is what this is really asserting.
        Assert.InRange(pty.BytesRead, 1, 1024 * 1024);
    }

    [Fact]
    public void ASessionDisposedWhileTheReaderIsParked_TearsDownPromptly()
    {
        // The reader parks on a gate the pseudo-terminal knows nothing about, so ending the terminal
        // is no longer enough on its own to release it. Dispose waits two seconds for the thread
        // before giving up on it, and a teardown that takes that long is one where nothing woke it.
        var pty = new FloodPty();
        var session = TerminalSession.Start(
            () => pty, new XtermSharpEngineFactory(), Viewport, new BlackHoleDispatcher());

        Assert.True(
            pty.WaitUntilQuiet(TimeSpan.FromSeconds(5)),
            "the reader never parked");

        var clock = Stopwatch.StartNew();
        session.Dispose();
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"teardown took {clock.ElapsedMilliseconds} ms, which means the reader was left parked");
    }

    [Fact]
    public void OutputLargerThanTheBound_ArrivesWholeOnceItIsDrained()
    {
        // The bound is a delay, never a loss: the buffers are swapped rather than copied out of, and
        // a swap that dropped what the reader had just written would show up here as a short screen.
        // Sized past the 256 KB bound on purpose, so the reader has to park and be released at least
        // once on the way through.
        const int Lines = 30000;

        var text = new StringBuilder();
        for (var line = 0; line < Lines; line++)
            text.Append($"line {line}\r\n");

        var content = Encoding.UTF8.GetBytes(text.ToString());
        Assert.True(content.Length > 256 * 1024, "the flood has to be larger than the bound to test it");

        var pty = new FloodPty(content, repeat: false);
        var dispatcher = new QueuedDispatcher();
        using var session = TerminalSession.Start(
            () => pty, new XtermSharpEngineFactory(), Viewport, dispatcher);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            dispatcher.Drain();
            if (pty.Finished && dispatcher.Queued == 0) break;
            Thread.Sleep(1);
        }

        dispatcher.Drain();
        Assert.True(pty.Finished, "the flood never finished being read");

        // The last line the shell wrote is the one before the cursor's, and it is the one a lost
        // final swap would take with it.
        var row = new TerminalCell[Viewport.Columns];
        session.Grid.CopyRow(session.Grid.Size.Rows - 2, row);
        Assert.Equal($"line {Lines - 1}", Row(row));
    }

    static string Row(TerminalCell[] cells)
    {
        var text = new StringBuilder();
        foreach (var cell in cells) text.Append(cell.Rune.ToString());
        return text.ToString().TrimEnd();
    }

    /// <summary>A dispatcher that takes posts and never runs them, so nothing is ever drained.</summary>
    sealed class BlackHoleDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
        }
    }
}

/// <summary>
/// A pseudo-terminal with more to say than anyone will read, which reports how much was taken from
/// it and when it was last asked.
/// </summary>
internal sealed class FloodPty : IPtySession
{
    readonly byte[] _content;
    readonly bool _repeat;
    readonly TaskCompletionSource<PtyExit> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();

    long _bytesRead;
    long _offset;
    DateTime _lastRead = DateTime.UtcNow;
    volatile bool _disposed;

    public FloodPty(byte[]? content = null, bool repeat = true)
    {
        _content = content ?? Encoding.UTF8.GetBytes(new string('y', 4096));
        _repeat = repeat;
    }

    public Task<PtyExit> Exited => _exited.Task;

    public long BytesRead
    {
        get { lock (_gate) return _bytesRead; }
    }

    public bool Finished
    {
        get { lock (_gate) return !_repeat && _offset >= _content.Length; }
    }

    /// <summary>Waits until nothing has been read for long enough to call the reader stopped.</summary>
    public bool WaitUntilQuiet(TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;

        while (DateTime.UtcNow < deadline)
        {
            DateTime last;
            lock (_gate) last = _lastRead;

            if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(300)) return true;
            Thread.Sleep(20);
        }

        return false;
    }

    public int ReadOutput(Span<byte> buffer)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FloodPty));

        lock (_gate)
        {
            // Zero is the end of the stream, which is how a pseudo-terminal reports a shell that has
            // finished writing — the reader stops, and whatever it already handed on is still queued.
            if (!_repeat && _offset >= _content.Length) return 0;

            var from = (int)(_offset % _content.Length);
            var count = Math.Min(buffer.Length, _content.Length - from);
            _content.AsSpan(from, count).CopyTo(buffer);

            _offset += count;
            _bytesRead += count;
            _lastRead = DateTime.UtcNow;
            return count;
        }
    }

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
    }

    public void Resize(PtySize size)
    {
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exited.TrySetResult(new PtyExit.Completed(0));
    }
}
