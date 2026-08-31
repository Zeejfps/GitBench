using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// One shell running on a pseudo-terminal, joined to the engine that turns its output into a grid.
/// </summary>
/// <remarks>
/// <para>
/// The two halves this joins have no idea about each other — <c>IPtySession</c> moves bytes and
/// <c>ITerminalEngine</c> parses them — and everything that has to be true at the join lives here:
/// which thread the engine is touched from, how much output becomes one repaint, and that a reply
/// the engine produces gets written back up the terminal.
/// </para>
/// <para>
/// The engine is only ever touched on the UI thread. The reader thread does nothing but move bytes
/// into a buffer, so <see cref="Grid"/> and <see cref="State"/> can be read straight from a draw
/// without a lock and without a snapshot copy of the whole screen every frame.
/// </para>
/// </remarks>
internal sealed class TerminalSession : IDisposable
{
    const int ReadBufferBytes = 64 * 1024;

    /// <summary>How far the reader is allowed to run ahead of the UI thread before it is parked.</summary>
    /// <remarks>
    /// A shell can produce faster than the engine can parse — <c>yes</c> outruns it by an order of
    /// magnitude — so without a bound the queue grows at the difference for as long as the command
    /// runs. That is not a slow terminal but a diverging one: memory climbs until the process dies,
    /// and every drain takes longer than the last because the batch it feeds keeps growing. Parking
    /// the reader hands the backpressure down the pseudo-terminal to the child, which is where a
    /// program that outruns its terminal is supposed to be slowed. The bound doubles as the cap on
    /// how long one drain can hold the UI thread, so it is deliberately far below the point where
    /// memory is a concern.
    /// </remarks>
    const int PendingHighWaterBytes = 256 * 1024;

    const int DefaultScrollbackLines = 5000;

    const int OutgoingBufferBytes = 4 * 1024;

    readonly IPtySession _pty;
    readonly ITerminalEngine _engine;
    readonly IUiDispatcher _dispatcher;
    readonly IClipboard? _clipboard;
    readonly Thread _reader;
    readonly Thread _writer;
    readonly Lock _gate = new();
    readonly Lock _outgoingGate = new();
    readonly ManualResetEventSlim _room = new(true);
    readonly ManualResetEventSlim _outgoingReady = new(false);

    // Two buffers swapped under the gate rather than one copied out of: the reader fills whichever
    // is current while the UI thread parses the one it took, so a drain costs a reference swap
    // instead of an allocation and a copy of the whole backlog.
    byte[] _pending = new byte[ReadBufferBytes];
    byte[] _spare = new byte[ReadBufferBytes];
    int _pendingCount;

    byte[] _outgoing = new byte[OutgoingBufferBytes];
    byte[] _outgoingSpare = new byte[OutgoingBufferBytes];
    int _outgoingCount;

    bool _drainQueued;
    bool _writing;
    bool _disposed;
    bool _faulted;
    int _scrollOffset;
    TerminalSpan? _selection;

    TerminalSession(
        IPtySession pty,
        ITerminalEngine engine,
        IUiDispatcher dispatcher,
        IClipboard? clipboard)
    {
        _pty = pty;
        _engine = engine;
        _dispatcher = dispatcher;
        _clipboard = clipboard;

        _reader = new Thread(Read) { IsBackground = true, Name = "terminal-pty-reader" };
        _reader.Start();

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "terminal-pty-writer" };
        _writer.Start();
    }

    /// <summary>
    /// Spawns the shell and starts reading it. Blocking, and slow enough to be worth keeping off
    /// the UI thread: it waits on a process launch.
    /// </summary>
    /// <exception cref="PtySpawnException">The machine could not start the shell.</exception>
    public static TerminalSession Start(
        IPtySessionFactory sessions,
        ITerminalEngineFactory engines,
        PtySessionOptions options,
        IUiDispatcher dispatcher,
        int scrollbackLines = DefaultScrollbackLines,
        IClipboard? clipboard = null,
        ITerminalPalette? palette = null) =>
        Start(
            () => sessions.Start(options),
            engines,
            new TerminalSize(options.Size.Columns, options.Size.Rows),
            dispatcher,
            scrollbackLines,
            clipboard,
            palette);

    /// <summary>
    /// Starts on whatever pseudo-terminal <paramref name="open"/> produces, for a caller that has
    /// one rather than a spawn to perform.
    /// </summary>
    /// <remarks>
    /// The terminal is opened by a delegate so that a failure to open it disposes the engine that
    /// was built to parse it, rather than leaking one on every failed start.
    /// </remarks>
    public static TerminalSession Start(
        Func<IPtySession> open,
        ITerminalEngineFactory engines,
        TerminalSize size,
        IUiDispatcher dispatcher,
        int scrollbackLines = DefaultScrollbackLines,
        IClipboard? clipboard = null,
        ITerminalPalette? palette = null)
    {
        var engine = engines.Create(new TerminalSetup(size, scrollbackLines) { Palette = palette });

        try
        {
            return new TerminalSession(open(), engine, dispatcher, clipboard);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>The live screen. Read on the UI thread only, and only between drains.</summary>
    public ITerminalGrid Grid => _engine.Grid;

    public TerminalState State => _engine.State;

    /// <summary>
    /// How many lines above the live screen the viewport is showing. Zero means it is following the
    /// shell, which is where it sits unless the user has scrolled back.
    /// </summary>
    /// <remarks>
    /// The scroll position lives here rather than in the engine, which deliberately has none: where
    /// the user has scrolled to is a property of this screen, not of the bytes, and an engine that
    /// held it would make the same grid read differently depending on the UI. It lives here rather
    /// than in the view because following the shell is a rule about output arriving, and this is the
    /// only place that sees output arrive.
    /// </remarks>
    public int ScrollOffset => _scrollOffset;

    /// <summary>
    /// Moves the viewport through the history. Positive goes back towards the oldest line, negative
    /// forwards towards the shell. Returns whether it actually moved, so a caller can leave a wheel
    /// event to whatever scrolls behind it rather than swallowing one that did nothing.
    /// </summary>
    public bool Scroll(int lines)
    {
        if (_disposed) return false;

        var target = Math.Clamp((long)_scrollOffset + lines, 0, Grid.ScrollbackRows);
        if (target == _scrollOffset) return false;

        _scrollOffset = (int)target;
        return true;
    }

    /// <summary>Moves the viewport by whole screens, one line short so the reader keeps a landmark.</summary>
    public bool ScrollPages(int pages) => Scroll(pages * Math.Max(1, Grid.Size.Rows - 1));

    /// <summary>
    /// The text the user has highlighted, in the grid's own coordinates, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Here rather than in the view for the reason the scroll offset is: output moves the text under
    /// a selection, and this is the only place that sees output arrive. It also outlives the pane,
    /// which is rebuilt on every repository switch.
    /// </remarks>
    public TerminalSpan? Selection => _selection;

    /// <summary>
    /// Highlights from <paramref name="anchor"/> to <paramref name="focus"/>. Returns whether the
    /// selection changed.
    /// </summary>
    public bool Select(GridPoint anchor, GridPoint focus, SelectionGranularity granularity)
    {
        if (_disposed) return false;

        var span = TerminalSelectionText.Resolve(Grid, anchor, focus, granularity);
        if (span == _selection) return false;

        _selection = span;
        return true;
    }

    public bool ClearSelection()
    {
        if (_selection is null) return false;

        _selection = null;
        return true;
    }

    /// <summary>The highlighted text, or an empty string when nothing is highlighted.</summary>
    public string SelectionText() =>
        _disposed || _selection is not { } span ? string.Empty : TerminalSelectionText.Build(Grid, span);

    /// <summary>Returns the viewport to the live screen, as typing does.</summary>
    public bool ScrollToBottom()
    {
        if (_scrollOffset == 0) return false;

        _scrollOffset = 0;
        return true;
    }

    /// <summary>
    /// Completes when the shell is gone, saying whether it finished on its own or the session ended
    /// it. Not the end of the screen: output arrives after this, and the reader keeps draining until
    /// the stream itself ends.
    /// </summary>
    public Task<PtyExit> Exited => _pty.Exited;

    /// <summary>
    /// Raised on the UI thread once the engine has taken a batch of output, whether or not anything
    /// visible changed.
    /// </summary>
    public event Action? Updated;

    /// <summary>
    /// Raised on the UI thread when reading the terminal or applying what it read failed, and the
    /// screen has therefore stopped following the shell. Never for an ordinary end of stream.
    /// </summary>
    /// <remarks>
    /// The reader runs on a thread of its own, where an escaping exception ends the process rather
    /// than the pane — and a drain runs on the thread that owns the window, where one takes the whole
    /// application down. Either failure has to be caught here whether or not anyone is listening. It
    /// is reported rather than swallowed because the alternative is a pane that silently stops
    /// updating and still takes keystrokes, which reads as the shell having hung.
    /// </remarks>
    public event Action<string>? Faulted;

    /// <summary>Sends bytes to the shell as terminal input.</summary>
    /// <remarks>
    /// Queued for the writer thread rather than written here. The master is a blocking descriptor and
    /// a shell at a prompt has about a kilobyte of line discipline to take: writing a paste on the UI
    /// thread stops the window until the child has read all of it.
    /// </remarks>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || bytes.IsEmpty) return;

        lock (_outgoingGate)
        {
            var needed = _outgoingCount + bytes.Length;

            if (needed > _outgoing.Length)
            {
                var grown = new byte[Math.Max(needed, _outgoing.Length * 2)];
                Buffer.BlockCopy(_outgoing, 0, grown, 0, _outgoingCount);
                _outgoing = grown;
            }

            bytes.CopyTo(_outgoing.AsSpan(_outgoingCount));
            _outgoingCount = needed;
            _outgoingReady.Set();
        }
    }

    /// <summary>
    /// Waits until everything written has reached the pseudo-terminal, or the timeout runs out.
    /// Returns whether it drained.
    /// </summary>
    /// <remarks>
    /// The queue is what keeps a bulk write off the UI thread, and it is also what makes "the shell
    /// has read this" stop being true the moment <see cref="Write"/> returns. Anything that needs
    /// the old guarantee — a test asserting on what the shell received — asks for it here.
    /// </remarks>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            lock (_outgoingGate)
            {
                if (_outgoingCount == 0 && !_writing) return true;
            }

            if (Environment.TickCount64 >= deadline) return false;

            Thread.Sleep(1);
        }
    }

    void WriteLoop()
    {
        while (true)
        {
            try
            {
                _outgoingReady.Wait();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            byte[] batch;
            int count;

            lock (_outgoingGate)
            {
                count = _outgoingCount;

                if (count == 0)
                {
                    if (_disposed) return;
                    _outgoingReady.Reset();
                    continue;
                }

                batch = _outgoing;
                _outgoing = _outgoingSpare;
                _outgoingSpare = batch;
                _outgoingCount = 0;
                _writing = true;
            }

            try
            {
                _pty.WriteInput(batch.AsSpan(0, count));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception failure)
            {
                Report(failure.Message);
                return;
            }
            finally
            {
                lock (_outgoingGate) _writing = false;
            }
        }
    }

    /// <summary>
    /// Tells both halves the viewport is a different shape. Both, and in this order: the engine
    /// reflows the grid a draw is about to read, and the shell needs its SIGWINCH to redraw into
    /// the new size.
    /// </summary>
    public void Resize(TerminalSize size)
    {
        if (_disposed || size == Grid.Size) return;

        _engine.Resize(size);
        ClampScroll();
        _selection = null;

        try
        {
            _pty.Resize(new PtySize(size.Columns, size.Rows));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ends the output stream, which releases the reader from its blocking read — the whole
        // reason this needs no cancellable I/O.
        _pty.Dispose();

        // A disposed pseudo-terminal releases a reader blocked in a read, but not one parked waiting
        // for room: this is the only thing that wakes that one, and without it teardown waits out
        // the join's full patience.
        _room.Set();
        _outgoingReady.Set();

        var stopped = _reader.Join(TimeSpan.FromSeconds(2));
        var writerStopped = _writer.Join(TimeSpan.FromSeconds(2));
        _engine.Dispose();

        // Only once the reader is known to have finished with it. A thread still running would take
        // an ObjectDisposedException on its next wait, at the top of a thread, which is fatal to the
        // process — worth strictly less than leaking one event for a reader that already hung.
        if (stopped) _room.Dispose();
        if (writerStopped) _outgoingReady.Dispose();
    }

    /// <remarks>
    /// Grown by doubling and never shrunk: the buffer settles at the largest burst a session sees,
    /// which the high-water mark keeps small, and a terminal that reallocated per read would undo
    /// the point of buffering at all.
    /// </remarks>
    void Append(byte[] source, int count)
    {
        var needed = _pendingCount + count;

        if (needed > _pending.Length)
        {
            var grown = new byte[Math.Max(needed, _pending.Length * 2)];
            Buffer.BlockCopy(_pending, 0, grown, 0, _pendingCount);
            _pending = grown;
        }

        Buffer.BlockCopy(source, 0, _pending, _pendingCount, count);
        _pendingCount = needed;
    }

    void Read()
    {
        var buffer = new byte[ReadBufferBytes];

        while (true)
        {
            // Parked while the UI thread is behind, which stops this thread draining the terminal's
            // own queue into an unbounded one of ours and leaving the child free to run flat out.
            _room.Wait();

            int read;
            try
            {
                read = _pty.ReadOutput(buffer);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception failure)
            {
                // Deliberately every exception, which is not the usual licence: this is the top of a
                // thread, so anything that escapes takes the process down instead of the pane. The
                // platforms do not agree on the type either — a Win32Exception from ConPTY, an
                // IOException from an unexpected errno — and a terminal that stops updating is worth
                // strictly less than one that says why.
                Report(failure.Message);
                return;
            }

            // Zero is the end of the session's output, which arrives after the child has exited and
            // after the flush that follows it — not when the child exits.
            if (read <= 0) return;

            bool queue;
            lock (_gate)
            {
                Append(buffer, read);
                if (_pendingCount >= PendingHighWaterBytes) _room.Reset();
                queue = !_drainQueued;
                _drainQueued = true;
            }

            // One post per drain, not one per read. A `cat` of a large file arrives as thousands of
            // reads, and posting each would put thousands of feeds and repaints on the UI thread to
            // show the one screen they add up to.
            if (queue) _dispatcher.Post(Drain);
        }
    }

    /// <remarks>
    /// Posted rather than raised where it was caught, for the reason every other notification here is
    /// posted: the pane's state is the UI thread's. A session already disposed says nothing, since a
    /// read that fails because the session is going away is the teardown working.
    /// </remarks>
    void Report(string failure) =>
        _dispatcher.Post(() =>
        {
            if (!_disposed) Faulted?.Invoke(failure);
        });

    void Drain()
    {
        // Every path out of here releases the reader. A session that has stopped feeding is not a
        // reason to leave a thread parked on a gate nobody will open again.
        if (_disposed || _faulted)
        {
            _room.Set();
            return;
        }

        byte[] batch;
        int count;
        lock (_gate)
        {
            _drainQueued = false;
            count = _pendingCount;

            if (count == 0)
            {
                _room.Set();
                return;
            }

            batch = _pending;
            _pending = _spare;
            _spare = batch;
            _pendingCount = 0;
            _room.Set();
        }

        var alternateBefore = _engine.State.Modes.AlternateScreen;

        FeedResult result;
        try
        {
            result = _engine.Feed(batch.AsSpan(0, count));
        }
        catch (Exception failure)
        {
            _faulted = true;
            Faulted?.Invoke(failure.Message);
            return;
        }

        // Device-status and capability replies are the program's question answered; they go back up
        // the terminal as input, which is where the program is waiting for them.
        if (result.HasResponse) Write(result.Response.Span);
        if (result.HasClipboardRequests) ApplyClipboard(result.Clipboard.Span);

        FollowOutput(result.LinesScrolled);
        FollowSelection(result.LinesScrolled, alternateBefore, _engine.State.Modes.AlternateScreen);

        Updated?.Invoke();
    }

    /// <summary>
    /// Keeps whatever the reader is looking at still while the shell writes underneath it. Output
    /// pushes the screen's contents up past the viewport, so a scroll position left alone would have
    /// the text crawling under a reader who has not touched the wheel.
    /// </summary>
    /// <remarks>
    /// Only while scrolled back. At the bottom the viewport follows the shell, which is the whole
    /// point of being at the bottom.
    /// </remarks>
    void FollowOutput(int linesScrolled)
    {
        if (_scrollOffset > 0 && linesScrolled > 0)
            _scrollOffset = (int)Math.Min((long)_scrollOffset + linesScrolled, int.MaxValue);

        ClampScroll();
    }

    /// <summary>
    /// Pulls the scroll position back into the history that exists. The history shrinks under it in
    /// more than one way — the alternate screen has none at all, a resize reflows it — and every one
    /// of them ends in a feed or a resize.
    /// </summary>
    void ClampScroll() => _scrollOffset = Math.Clamp(_scrollOffset, 0, Grid.ScrollbackRows);

    /// <summary>
    /// Puts what a program sent through OSC 52 on the system clipboard.
    /// </summary>
    /// <remarks>
    /// The write half only. A read never reaches here: the engine answers it with an empty clipboard
    /// so that a program asking is not left waiting, and does not surface it, because a program
    /// running in this pane reading what the user last copied is exfiltration with a terminal
    /// sequence for a lever.
    /// </remarks>
    void ApplyClipboard(ReadOnlySpan<TerminalClipboardRequest> requests)
    {
        if (_clipboard is null) return;

        foreach (var request in requests)
        {
            if (request.Target != ClipboardTarget.Clipboard) continue;
            if (ClipboardText.FromProgram(request.Text) is not { } text) continue;

            try
            {
                _clipboard.SetText(text.Value);
            }
            catch (Exception)
            {
                // A clipboard that will not take the text is not worth faulting a shell over.
            }
        }
    }

    /// <summary>
    /// Carries the selection with the text it covers, and drops it when that text is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only on the normal screen. <c>LinesScrolled</c> counts lines leaving the top of the screen on
    /// the alternate buffer too, where there is no history for them to leave into, so shifting by it
    /// there walks a selection off a grid whose only rows are the visible ones. A full-screen program
    /// under a scroll region does not increment it at all while its text still moves, so the
    /// alternate screen's selection is positional and stale by design — which is what every terminal
    /// does with it.
    /// </para>
    /// <para>
    /// Dropped rather than clamped when the text scrolls out of the history. Clamping would move the
    /// ends onto rows the user never highlighted and copy text they never selected.
    /// </para>
    /// </remarks>
    void FollowSelection(int linesScrolled, bool alternateBefore, bool alternateAfter)
    {
        if (_selection is not { } span) return;

        if (alternateBefore != alternateAfter)
        {
            _selection = null;
            return;
        }

        var bounds = GridBounds.Of(Grid);

        _selection = alternateAfter
            ? TerminalSpan.Surviving(span, bounds)
            : TerminalSpan.Shift(span, linesScrolled, bounds);
    }
}
