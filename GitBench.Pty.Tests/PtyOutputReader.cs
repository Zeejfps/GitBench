using System.Diagnostics;

namespace GitBench.Pty.Tests;

/// <summary>
/// Drains <see cref="IPtySession.Output"/> on a dedicated thread — the way the real terminal will —
/// and lets a test wait, with a hard deadline, for text to appear or for the stream to end.
/// </summary>
sealed class PtyOutputReader
{
    readonly object _gate = new();
    readonly MemoryStream _received = new();
    bool _ended;
    Exception? _failure;

    public PtyOutputReader(Stream output)
    {
        var thread = new Thread(() => Pump(output))
        {
            IsBackground = true,
            Name = "pty-test-reader",
        };

        thread.Start();
    }

    /// <summary>True once <paramref name="expected"/> has been printed, false if the deadline passes first.</summary>
    public bool WaitFor(string expected, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();

        lock (_gate)
        {
            while (true)
            {
                if (VtText.Contains(DecodeLocked(), expected))
                    return true;

                if (_ended)
                    return false;

                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return false;

                Monitor.Wait(_gate, remaining);
            }
        }
    }

    /// <summary>True once the stream ended cleanly, false if the deadline passes or a read threw.</summary>
    public bool WaitForEndOfStream(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();

        lock (_gate)
        {
            while (!_ended)
            {
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return false;

                Monitor.Wait(_gate, remaining);
            }

            return _failure is null;
        }
    }

    /// <summary>What the terminal has shown so far, for a failure message.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            var decoded = DecodeLocked();
            var tail = decoded.Length > 4000 ? decoded[^4000..] : decoded;
            return _failure is null ? tail : $"{tail}\n[reader failed: {_failure}]";
        }
    }

    void Pump(Stream output)
    {
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                var read = output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                lock (_gate)
                {
                    _received.Write(buffer, 0, read);
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _failure = ex;
        }
        finally
        {
            lock (_gate)
            {
                _ended = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    string DecodeLocked() => VtText.Decode(_received.GetBuffer().AsSpan(0, (int)_received.Length));
}
