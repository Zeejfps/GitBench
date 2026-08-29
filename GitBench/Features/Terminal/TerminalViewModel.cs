using GitBench.Infrastructure;
using GitBench.Terminal.Vt;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>What the terminal pane is showing.</summary>
internal abstract record TerminalRenderState
{
    /// <summary>Before a shell exists: either the viewport has not been measured yet, or it has and
    /// the spawn is in flight. The pane shows the same thing for both, so they are one state.</summary>
    public sealed record Starting : TerminalRenderState;

    public sealed record Running(TerminalSession Session) : TerminalRenderState;

    public sealed record Failed(string Message) : TerminalRenderState;
}

internal sealed record TerminalPaneState(TerminalRenderState Render);

/// <summary>
/// The terminal pane's shell: starts one for a repository and hands the view whichever of the three
/// things it should be drawing.
/// </summary>
/// <remarks>
/// <para>
/// The shell is not started until the view reports a viewport, because until then nobody knows what
/// size to start it at. Guessing costs more than waiting one frame: a shell spawned at 80x24 into a
/// wider pane draws its prompt at the wrong width and then, on the resize that follows, ConPTY
/// re-emits its whole buffer — so the first thing the user sees is a redraw of a mistake.
/// </para>
/// <para>
/// The viewport is reported from a draw rather than a layout pass because a cell size can only be
/// measured against the canvas that will draw it.
/// </para>
/// </remarks>
internal sealed class TerminalViewModel : ViewModelBase<TerminalPaneState>, ITerminalInput
{
    readonly ITerminalLaunch _launch;

    TerminalSession? _session;
    TerminalSize? _size;
    bool _starting;
    bool _shellExited;
    bool _closed;

    public TerminalViewModel(ITerminalLaunch launch, IUiDispatcher dispatcher)
        : base(dispatcher, new TerminalPaneState(new TerminalRenderState.Starting()))
    {
        _launch = launch;

        RenderState = Slice(s => s.Render);
    }

    public IReadable<TerminalRenderState> RenderState { get; }

    /// <summary>Raised on the UI thread when the screen has changed and wants drawing again.</summary>
    public event Action? Updated;

    /// <summary>True while there is a live shell to take input.</summary>
    /// <remarks>
    /// False again once the shell exits on its own, not only once the pane is disposed. A pane still
    /// claiming keys for a shell that has gone eats every keystroke in the window until the user
    /// thinks to click elsewhere, with nothing on screen saying why.
    /// </remarks>
    public bool IsAcceptingInput => LiveSession is not null;

    /// <summary>
    /// The shell's current modes, which decide how a key is encoded. The default modes when there is
    /// no shell, which encode nothing anyone can read.
    /// </summary>
    public TerminalModes Modes => LiveSession?.State.Modes ?? default;

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
        if (session.ScrollToBottom()) Updated?.Invoke();
    }

    /// <summary>
    /// Moves the viewport through the history. Still works once the shell has exited, because the
    /// screen it left behind is exactly what a reader wants to scroll back through.
    /// </summary>
    public bool Scroll(int lines) => Moved(_session?.Scroll(lines));

    /// <summary>Moves the viewport by whole screens.</summary>
    public bool ScrollPages(int pages) => Moved(_session?.ScrollPages(pages));

    bool Moved(bool? moved)
    {
        if (moved is not true) return false;

        Updated?.Invoke();
        return true;
    }

    TerminalSession? LiveSession => _shellExited ? null : _session;

    /// <summary>
    /// Tells the view model how many cells the pane can show. The first call starts the shell; later
    /// ones resize it.
    /// </summary>
    public void ReportViewport(TerminalSize viewport)
    {
        // What the pane can show and what the terminal runs at are not always the same number: a
        // replayed recording keeps the size it was recorded at whatever the pane does.
        var size = _launch.SizeFor(viewport);
        if (_closed || _size == size) return;
        _size = size;

        if (_session is { } session)
        {
            session.Resize(size);
            return;
        }

        if (_starting) return;
        _starting = true;
        Start(size);
    }

    void Start(TerminalSize size)
    {
        var dispatcher = Dispatcher;

        // Not RunBackground: a spawn that lands after disposal has produced a live shell, and the
        // base runner drops a stale continuation without giving it back — which would leak the
        // process. This one hands it over either way, and disposes it if there is nobody left to
        // own it.
        Task.Run(() =>
        {
            try
            {
                var session = _launch.Start(size, dispatcher);
                dispatcher.Post(() => Adopt(session));
            }
            catch (Exception ex)
            {
                dispatcher.Post(() => Fail(ex.Message));
            }
        });
    }

    void Adopt(TerminalSession session)
    {
        if (_closed)
        {
            session.Dispose();
            return;
        }

        _session = session;
        _starting = false;
        session.Updated += OnSessionUpdated;
        session.Faulted += Fail;

        // The pane may have been resized while the shell was starting, in which case the size it
        // was spawned at is already wrong.
        if (_size is { } size) session.Resize(size);

        Update(s => s with { Render = new TerminalRenderState.Running(session) });

        WatchForExit(session);
    }

    // Unconditional, including for a session that has already finished: a shell can exit between the
    // spawn and here, and skipping the watch for it leaves the pane claiming keys forever.
    void WatchForExit(TerminalSession session)
    {
        var dispatcher = Dispatcher;

        session.Exited.ContinueWith(
            _ => dispatcher.Post(() => _shellExited = true),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    void Fail(string message)
    {
        if (_closed) return;

        _starting = false;
        Update(s => s with { Render = new TerminalRenderState.Failed(message) });
    }

    void OnSessionUpdated() => Updated?.Invoke();

    public override void Dispose()
    {
        _closed = true;

        if (_session is { } session)
        {
            session.Updated -= OnSessionUpdated;
            session.Faulted -= Fail;
            session.Dispose();
            _session = null;
        }

        base.Dispose();
    }
}
