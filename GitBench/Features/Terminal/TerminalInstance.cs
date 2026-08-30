using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>What a terminal is doing, and therefore what the pane shows for it.</summary>
/// <remarks>
/// Six states rather than a session plus a handful of flags, because the flags could disagree: a
/// session that had exited used to sit behind <c>Running</c> with a bool beside it saying otherwise,
/// and every reader had to remember to consult both. Whether input is accepted, whether the screen
/// is drawn and whether a shell can be started are each one question about this one value.
/// </remarks>
internal abstract record TerminalRenderState
{
    /// <summary>No shell, and none asked for. The pane offers to start one.</summary>
    public sealed record Idle : TerminalRenderState;

    /// <summary>A shell has been asked for: either the viewport has not been measured yet, or it has
    /// and the spawn is in flight. The pane shows the same thing for both, so they are one state.</summary>
    public sealed record Starting : TerminalRenderState;

    public sealed record Running(TerminalSession Session) : TerminalRenderState;

    /// <summary>The shell finished. The screen it left is kept and stays scrollable — reading back
    /// through what a command printed is most wanted once it has finished printing it.</summary>
    public sealed record Exited(TerminalSession Session) : TerminalRenderState;

    /// <summary>Reading the terminal failed under a shell that had started. Distinct from
    /// <see cref="Failed"/> because there is a screen to keep showing: the output that arrived
    /// before the failure is still the last thing the shell said.</summary>
    public sealed record Faulted(TerminalSession Session, string Message) : TerminalRenderState;

    /// <summary>The shell never started. There is no screen, only the reason.</summary>
    public sealed record Failed(string Message) : TerminalRenderState;
}

/// <summary>
/// One repository's terminal: the shell it is running, or the offer to start one.
/// </summary>
/// <remarks>
/// <para>
/// Owned by <see cref="ITerminalSessionStore"/> rather than by a view, because everything here has
/// to outlive the pane that shows it — a shell keeps running while the user is in another repository
/// or another mode, and a spawn in flight when the pane unmounts still lands.
/// </para>
/// <para>
/// The shell is not started until something asks for it, and not spawned until the view reports a
/// viewport, because until then nobody knows what size to start it at. Guessing costs more than
/// waiting one frame: a shell spawned at 80x24 into a wider pane draws its prompt at the wrong width
/// and then, on the resize that follows, ConPTY re-emits its whole buffer — so the first thing the
/// user sees is a redraw of a mistake.
/// </para>
/// <para>
/// The viewport is reported from a draw rather than a layout pass because a cell size can only be
/// measured against the canvas that will draw it.
/// </para>
/// </remarks>
internal sealed class TerminalInstance : IDisposable, ITerminalInput
{
    readonly ITerminalLaunch _launch;
    readonly IUiDispatcher _dispatcher;
    readonly State<TerminalRenderState> _render = new(new TerminalRenderState.Idle());
    readonly State<string?> _title = new(null);
    readonly State<string?> _givenName = new(null);

    // Guards the handover of a spawned session from the worker that made it to the UI thread that
    // adopts it, and nothing else. See Spawn.
    readonly Lock _handoff = new();

    TerminalSession? _session;
    TerminalSize? _size;
    TerminalSession? _spawned;
    bool _startRequested;
    bool _disposed;

    public TerminalInstance(ITerminalLaunch launch, IUiDispatcher dispatcher)
    {
        _launch = launch;
        _dispatcher = dispatcher;
    }

    public IReadable<TerminalRenderState> Render => _render;

    /// <summary>
    /// What the terminal calls itself: what OSC 0/2 last set, and null when nothing has set one.
    /// </summary>
    /// <remarks>
    /// Observable rather than read off <see cref="TerminalSession.State"/> at the point of use,
    /// because a title changes on output arriving and nothing about a caller's own state says so.
    /// A shell writes one and every program it runs overwrites it, which is what makes a strip of
    /// tabs legible: the label follows the running command rather than naming four identical shells.
    /// </remarks>
    public IReadable<string?> Title => _title;

    /// <summary>What this terminal is called before anything running in it has said. See
    /// <see cref="ITerminalLaunch.Name"/>.</summary>
    public string Name => _launch.Name;

    /// <summary>
    /// The name the user gave this terminal, and null when they have given none.
    /// </summary>
    /// <remarks>
    /// Outranks <see cref="Title"/>, which is why it is a separate value rather than a write into it:
    /// a program that sets a title while a renamed tab is on screen must not take the name back, and
    /// dropping the name has to reveal whatever the title says *now* rather than whatever it said
    /// when the rename happened.
    /// </remarks>
    public IReadable<string?> GivenName => _givenName;

    /// <summary>
    /// Names this terminal, or — for a blank name — hands it back to its title and its shell.
    /// </summary>
    /// <remarks>
    /// Blank is not a name a tab could show, and a field the user emptied reads as asking for the
    /// name it had before they touched it. Normalised here rather than at the dialog, so no caller
    /// can put whitespace into a strip.
    /// </remarks>
    public void Rename(string? name)
    {
        if (_disposed) return;

        var trimmed = name?.Trim();
        _givenName.Value = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Raised on the UI thread when the screen has changed and wants drawing again.</summary>
    public event Action? Updated;

    /// <summary>True while there is a live shell to take input.</summary>
    /// <remarks>
    /// False again once the shell exits on its own, not only once the terminal is disposed. A pane
    /// still claiming keys for a shell that has gone eats every keystroke in the window until the
    /// user thinks to click elsewhere, with nothing on screen saying why.
    /// </remarks>
    public bool IsAcceptingInput => _render.Value is TerminalRenderState.Running;

    /// <summary>Whether <see cref="Start"/> would do anything: there is no shell, or the one there
    /// was has finished.</summary>
    public bool CanStart => _render.Value is not (TerminalRenderState.Running or TerminalRenderState.Starting);

    /// <summary>
    /// Whether there is a shell process here that disposing this terminal would kill — the question
    /// asked before something closes the application or drops the repository.
    /// </summary>
    /// <remarks>
    /// Not read off the render state, because two of them lie about it in opposite directions.
    /// <see cref="TerminalRenderState.Faulted"/> means reading the terminal failed, not that the
    /// shell did: the child is untouched and still needs ending, so a faulted terminal counts.
    /// <see cref="TerminalRenderState.Starting"/> covers a spawn that has been asked for and one
    /// already in flight, neither of which holds a session to ask. What is left is the session
    /// itself, and the only thing that knows whether its child is gone is
    /// <see cref="TerminalSession.Exited"/>.
    /// </remarks>
    public bool HasLiveShell =>
        !_disposed
        && (_render.Value is TerminalRenderState.Starting
            || (_session is { } session && !session.Exited.IsCompleted));

    /// <summary>
    /// The shell's current modes, which decide how a key is encoded. The default modes when there is
    /// no shell, which encode nothing anyone can read.
    /// </summary>
    public TerminalModes Modes => LiveSession?.State.Modes ?? default;

    /// <summary>
    /// Asks for a shell. Spawns immediately if the pane has already said how big it is, and
    /// otherwise on the first viewport report that follows.
    /// </summary>
    /// <remarks>
    /// Starting after a shell has finished disposes the one that finished, which is what makes the
    /// pane's offer to start again mean a new shell rather than a second reader of a dead one. The
    /// screen goes with it: what is being asked for is a fresh terminal.
    /// </remarks>
    public void Start()
    {
        if (_disposed || !CanStart) return;

        Retire();
        _startRequested = true;
        _render.Value = new TerminalRenderState.Starting();

        if (_size is { } size) Spawn(size);
    }

    /// <summary>
    /// Sends bytes to the shell as terminal input, and returns the viewport to the live screen.
    /// Does nothing when there is no shell.
    /// </summary>
    /// <remarks>
    /// Typing brings the screen back rather than the controller remembering to ask for it, so that
    /// every path that reaches the shell — a key, a character, whatever pastes later — lands
    /// somewhere the sender can see. Only what the user sends: the engine's own replies to a
    /// program's questions go straight to the session, and a program asking the terminal its size
    /// must not yank the reader's place in the history.
    /// </remarks>
    public void SendInput(ReadOnlySpan<byte> bytes)
    {
        if (LiveSession is not { } session) return;

        session.Write(bytes);

        var moved = session.ScrollToBottom();
        var deselected = session.ClearSelection();
        if (moved || deselected) Updated?.Invoke();
    }

    public void SendMouse(ReadOnlySpan<byte> bytes) => LiveSession?.Write(bytes);

    /// <summary>
    /// Sends pasted text to the shell, bracketed when the program has asked for it.
    /// </summary>
    public void Paste(string text)
    {
        if (LiveSession is not { } session) return;

        var bytes = TerminalPasteEncoder.Encode(text, session.State.Modes.BracketedPaste);
        if (bytes.Length == 0) return;

        SendInput(bytes);
    }

    /// <summary>
    /// Whether there is a screen to select text on. True once a shell has started, and still true
    /// after it exits — the screen a finished command left is what a reader most wants to copy.
    /// </summary>
    public bool HasScreen => Screen is not null;

    public TerminalSpan? Selection => Screen?.Selection;

    public bool Select(GridPoint anchor, GridPoint focus, SelectionGranularity granularity) =>
        Moved(Screen?.Select(anchor, focus, granularity));

    public bool ClearSelection() => Moved(Screen?.ClearSelection());

    public string SelectionText() => Screen?.SelectionText() ?? string.Empty;

    /// <summary>
    /// Moves the viewport through the history. Still works once the shell has exited, because the
    /// screen it left behind is exactly what a reader wants to scroll back through.
    /// </summary>
    public bool Scroll(int lines) => Moved(Screen?.Scroll(lines));

    /// <summary>Moves the viewport by whole screens.</summary>
    public bool ScrollPages(int pages) => Moved(Screen?.ScrollPages(pages));

    bool Moved(bool? moved)
    {
        if (moved is not true) return false;

        Updated?.Invoke();
        return true;
    }

    /// <summary>The session taking input: only one that is actually running.</summary>
    TerminalSession? LiveSession =>
        _render.Value is TerminalRenderState.Running running ? running.Session : null;

    /// <summary>The session there is a screen for, running or not.</summary>
    TerminalSession? Screen => _render.Value switch
    {
        TerminalRenderState.Running running => running.Session,
        TerminalRenderState.Exited exited => exited.Session,
        TerminalRenderState.Faulted faulted => faulted.Session,
        _ => null,
    };

    /// <summary>
    /// Tells the terminal how many cells the pane can show. Resizes a live shell, and starts one
    /// that was asked for before the pane knew its own size.
    /// </summary>
    public void ReportViewport(TerminalSize viewport)
    {
        // What the pane can show and what the terminal runs at are not always the same number: a
        // replayed recording keeps the size it was recorded at whatever the pane does.
        var size = _launch.SizeFor(viewport);
        if (_disposed || _size == size) return;
        _size = size;

        if (_session is { } session)
        {
            session.Resize(size);
            return;
        }

        if (_startRequested) Spawn(size);
    }

    void Spawn(TerminalSize size)
    {
        _startRequested = false;

        var launch = _launch;
        var dispatcher = _dispatcher;

        Task.Run(() =>
        {
            TerminalSession session;
            try
            {
                session = launch.Start(size, dispatcher);
            }
            catch (Exception ex)
            {
                dispatcher.Post(() => Fail(ex.Message));
                return;
            }

            // Recorded under the gate before it is posted, because a post is not a handover: the UI
            // loop can stop between the spawn and the adoption, in which case the posted work never
            // runs and a live shell is left owned by nobody. Disposal takes whatever is sitting
            // here, and a spawn that lands after disposal ends it on this thread.
            lock (_handoff)
            {
                if (!_disposed)
                {
                    _spawned = session;
                    dispatcher.Post(() => Adopt(session));
                    return;
                }
            }

            session.Dispose();
        });
    }

    void Adopt(TerminalSession session)
    {
        lock (_handoff)
        {
            if (ReferenceEquals(_spawned, session)) _spawned = null;
        }

        if (_disposed)
        {
            session.Dispose();
            return;
        }

        _session = session;
        session.Updated += OnSessionUpdated;
        session.Faulted += OnSessionFaulted;

        // The pane may have been resized while the shell was starting, in which case the size it
        // was spawned at is already wrong.
        if (_size is { } size) session.Resize(size);

        _render.Value = new TerminalRenderState.Running(session);
        SyncTitle();

        WatchForExit(session);
    }

    // Unconditional, including for a session that has already finished: a shell can exit between the
    // spawn and here, and skipping the watch for it leaves the terminal claiming keys forever.
    void WatchForExit(TerminalSession session)
    {
        var dispatcher = _dispatcher;

        session.Exited.ContinueWith(
            _ => dispatcher.Post(() => OnSessionExited(session)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    void OnSessionExited(TerminalSession session)
    {
        if (_disposed) return;
        if (_render.Value is not TerminalRenderState.Running running) return;
        if (!ReferenceEquals(running.Session, session)) return;

        _render.Value = new TerminalRenderState.Exited(session);
    }

    /// <remarks>
    /// A fault under a running shell keeps the screen; one raised for a session this terminal has
    /// already moved past is the failure of something nobody is showing, and says so without a
    /// screen to point at.
    /// </remarks>
    void OnSessionFaulted(string message)
    {
        if (_disposed) return;

        _render.Value = _render.Value is TerminalRenderState.Running running
            ? new TerminalRenderState.Faulted(running.Session, message)
            : new TerminalRenderState.Failed(message);
    }

    void Fail(string message)
    {
        if (_disposed) return;

        _startRequested = false;
        _render.Value = new TerminalRenderState.Failed(message);
    }

    void OnSessionUpdated()
    {
        SyncTitle();
        Updated?.Invoke();
    }

    /// <summary>
    /// Republishes the screen's title. Called wherever the screen changes rather than only on a
    /// feed, since a terminal that has just been retired still has a stale title to drop.
    /// </summary>
    void SyncTitle() =>
        _title.Value = Screen?.State.Title is { Length: > 0 } title ? title : null;

    /// <summary>Lets go of the session this terminal is holding, if any, and ends it.</summary>
    void Retire()
    {
        if (_session is not { } session) return;

        session.Updated -= OnSessionUpdated;
        session.Faulted -= OnSessionFaulted;
        session.Dispose();
        _session = null;
        _title.Value = null;
    }

    public void Dispose()
    {
        TerminalSession? pending;
        lock (_handoff)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _spawned;
            _spawned = null;
        }

        // A shell spawned but never adopted is still a process. Ended here rather than left to the
        // adoption that is not going to happen.
        pending?.Dispose();

        Retire();
        _render.Value = new TerminalRenderState.Idle();
        _render.Dispose();
        _title.Dispose();
        _givenName.Dispose();
    }
}
