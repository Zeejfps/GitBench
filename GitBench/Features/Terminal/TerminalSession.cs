using GitBench.Pty;
using GitBench.Terminal.Vt;
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
    const int DefaultScrollbackLines = 5000;

    readonly IPtySession _pty;
    readonly ITerminalEngine _engine;
    readonly IUiDispatcher _dispatcher;
    readonly Thread _reader;
    readonly Lock _gate = new();
    readonly List<byte> _pending = [];

    bool _drainQueued;
    bool _disposed;

    TerminalSession(IPtySession pty, ITerminalEngine engine, IUiDispatcher dispatcher)
    {
        _pty = pty;
        _engine = engine;
        _dispatcher = dispatcher;

        _reader = new Thread(Read) { IsBackground = true, Name = "terminal-pty-reader" };
        _reader.Start();
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
        int scrollbackLines = DefaultScrollbackLines) =>
        Start(
            () => sessions.Start(options),
            engines,
            new TerminalSize(options.Size.Columns, options.Size.Rows),
            dispatcher,
            scrollbackLines);

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
        int scrollbackLines = DefaultScrollbackLines)
    {
        var engine = engines.Create(new TerminalSetup(size, scrollbackLines));

        try
        {
            return new TerminalSession(open(), engine, dispatcher);
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
    /// Raised on the UI thread when reading the terminal failed and the screen has therefore stopped
    /// following the shell. At most once, and never for an ordinary end of stream.
    /// </summary>
    /// <remarks>
    /// The reader runs on a thread of its own, where an escaping exception ends the process rather
    /// than the pane — so the failure has to be caught here whether or not anyone is listening. It is
    /// reported rather than swallowed because the alternative is a pane that silently stops updating
    /// and still takes keystrokes, which reads as the shell having hung.
    /// </remarks>
    public event Action<string>? Faulted;

    /// <summary>Sends bytes to the shell as terminal input.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || bytes.IsEmpty) return;

        try
        {
            _pty.WriteInput(bytes);
        }
        catch (ObjectDisposedException)
        {
            // The shell is gone; there is nowhere for the keystroke to go and nothing to report.
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
        _reader.Join(TimeSpan.FromSeconds(2));
        _engine.Dispose();
    }

    void Read()
    {
        var buffer = new byte[ReadBufferBytes];

        while (true)
        {
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
                _pending.AddRange(buffer.AsSpan(0, read));
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
        if (_disposed) return;

        byte[] batch;
        lock (_gate)
        {
            _drainQueued = false;
            if (_pending.Count == 0) return;

            batch = _pending.ToArray();
            _pending.Clear();
        }

        var result = _engine.Feed(batch);

        // Device-status and capability replies are the program's question answered; they go back up
        // the terminal as input, which is where the program is waiting for them.
        if (result.HasResponse) Write(result.Response.Span);

        Updated?.Invoke();
    }
}
