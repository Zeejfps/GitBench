using GitBench.Infrastructure;
using GitBench.Pty;
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
internal sealed class TerminalViewModel : ViewModelBase<TerminalPaneState>
{
    readonly IPtySessionFactory _sessions;
    readonly ITerminalEngineFactory _engines;
    readonly string _workingDirectory;

    TerminalSession? _session;
    TerminalSize? _viewport;
    bool _starting;
    bool _closed;

    public TerminalViewModel(
        IPtySessionFactory sessions,
        ITerminalEngineFactory engines,
        IUiDispatcher dispatcher,
        string workingDirectory)
        : base(dispatcher, new TerminalPaneState(new TerminalRenderState.Starting()))
    {
        _sessions = sessions;
        _engines = engines;
        _workingDirectory = workingDirectory;

        RenderState = Slice(s => s.Render);
    }

    public IReadable<TerminalRenderState> RenderState { get; }

    /// <summary>Raised on the UI thread when the screen has changed and wants drawing again.</summary>
    public event Action? Updated;

    /// <summary>
    /// Tells the view model how many cells the pane can show. The first call starts the shell; later
    /// ones resize it.
    /// </summary>
    public void ReportViewport(TerminalSize size)
    {
        if (_closed || _viewport == size) return;
        _viewport = size;

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
        var options = ShellCommand.For(_workingDirectory, new PtySize(size.Columns, size.Rows));
        var dispatcher = Dispatcher;

        // Not RunBackground: a spawn that lands after disposal has produced a live shell, and the
        // base runner drops a stale continuation without giving it back — which would leak the
        // process. This one hands it over either way, and disposes it if there is nobody left to
        // own it.
        Task.Run(() =>
        {
            try
            {
                var session = TerminalSession.Start(_sessions, _engines, options, dispatcher);
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

        // The pane may have been resized while the shell was starting, in which case the size it
        // was spawned at is already wrong.
        if (_viewport is { } size) session.Resize(size);

        Update(s => s with { Render = new TerminalRenderState.Running(session) });
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
            session.Dispose();
            _session = null;
        }

        base.Dispose();
    }
}
