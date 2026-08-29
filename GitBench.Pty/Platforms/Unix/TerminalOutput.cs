namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// Holds what a pseudo-terminal has produced and nobody has asked for yet, handing it on in whatever
/// sizes the consumer asks for.
/// </summary>
/// <remarks>
/// <para>
/// A buffer of the session's own is not an optimisation. macOS discards a terminal's queued output
/// when the last descriptor on its slave closes — measured here with no process involved at all, a
/// write followed by a close leaves the master reading zero — so the last thing a child printed is
/// gone the instant it exits unless something had already read it. A consumer that arrives a moment
/// late would see an empty session, and a terminal pane busy painting when the shell exited would
/// lose the command's final line. Linux keeps those bytes readable and needs none of this, which is
/// exactly why it cannot be left to the platform.
/// </para>
/// <para>
/// Bounded, so that a child producing faster than anyone reads is slowed by this queue the way it
/// would otherwise be slowed by the kernel's, rather than growing it without limit.
/// </para>
/// </remarks>
internal sealed class TerminalOutput
{
    /// <summary>How far ahead of the consumer the terminal is allowed to run.</summary>
    public const int Capacity = 1 << 20;

    readonly object _gate = new();
    readonly Queue<byte[]> _chunks = new();

    State _state = new State.Open();
    int _consumed;
    int _pending;

    /// <summary>
    /// Blocks until there is room and then takes a copy, reporting false once the session has ended
    /// and there is no longer anywhere to put it.
    /// </summary>
    public bool Give(ReadOnlySpan<byte> bytes)
    {
        var chunk = bytes.ToArray();

        lock (_gate)
        {
            while (_pending >= Capacity && _state is State.Open)
                Monitor.Wait(_gate);

            if (_state is not State.Open)
                return false;

            _chunks.Enqueue(chunk);
            _pending += chunk.Length;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>
    /// Blocks until there is something to hand over, and returns 0 once the stream has finished.
    /// </summary>
    public int Take(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (_pending == 0 && _state is State.Open)
                Monitor.Wait(_gate);

            if (_state is State.Abandoned)
                return 0;

            var taken = Copy(buffer);

            if (taken > 0)
            {
                Monitor.PulseAll(_gate);
                return taken;
            }

            // Named arms rather than a catch-all, because the catch-all answer here is 0 and 0 is
            // "the stream is finished". A state added later would take that answer by default, on a
            // reader thread, with nothing raised anywhere to say so.
            return _state switch
            {
                State.Faulted faulted => throw faulted.Failure,
                State.Ended or State.Abandoned => 0,
                State.Open => 0,
                _ => throw new NotSupportedException($"No rule for reading a {_state} terminal."),
            };
        }
    }

    /// <summary>Reports that the terminal is finished; what is already here is still handed over.</summary>
    public void End() => Settle(new State.Ended());

    /// <summary>Reports that the terminal failed; what is already here is handed over first.</summary>
    public void Fail(Exception failure) => Settle(new State.Faulted(failure));

    /// <summary>
    /// Reports that the session was torn down, which discards what is here rather than handing it on:
    /// a read arriving after a disposal is owed the end of the stream, not a last look at it.
    /// </summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _state = new State.Abandoned();
            _chunks.Clear();
            _consumed = 0;
            _pending = 0;
            Monitor.PulseAll(_gate);
        }
    }

    void Settle(State settled)
    {
        lock (_gate)
        {
            if (_state is State.Open)
                _state = settled;

            Monitor.PulseAll(_gate);
        }
    }

    int Copy(Span<byte> buffer)
    {
        var taken = 0;

        while (taken < buffer.Length && _chunks.Count > 0)
        {
            var head = _chunks.Peek();
            var available = head.Length - _consumed;
            var copied = Math.Min(available, buffer.Length - taken);

            head.AsSpan(_consumed, copied).CopyTo(buffer[taken..]);

            taken += copied;
            _consumed += copied;
            _pending -= copied;

            if (_consumed < head.Length)
                continue;

            _chunks.Dequeue();
            _consumed = 0;
        }

        return taken;
    }

    abstract record State
    {
        State()
        {
        }

        public sealed record Open : State;

        public sealed record Ended : State;

        public sealed record Faulted(Exception Failure) : State;

        public sealed record Abandoned : State;
    }
}
