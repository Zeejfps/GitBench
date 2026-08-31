using GitBench.Pty;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// What a terminal pane runs, and at what size.
/// </summary>
/// <remarks>
/// The size is part of this rather than the pane's alone because not every terminal follows its
/// viewport. A shell does — it is told the window's shape and redraws into it. A recording does not:
/// bytes and geometry together are what determine a screen, so a session replayed at a size other
/// than the one it was recorded at wraps in different places and shows something that never
/// happened. Asking the launch what size to run at lets both be true without the view model
/// carrying a flag for which kind it has.
/// </remarks>
internal interface ITerminalLaunch
{
    /// <summary>
    /// What to call this terminal before a program running in it names itself — the shell's own
    /// file name for a shell, the recording's for a replay. The fallback behind
    /// <see cref="TerminalInstance.Title"/>, which is empty until something sets OSC 0/2.
    /// </summary>
    string Name { get; }

    /// <summary>The size to run at, given what the pane can currently show.</summary>
    TerminalSize SizeFor(TerminalSize viewport);

    /// <summary>
    /// Starts the terminal. Blocking, and called off the UI thread — a shell launch waits on a
    /// process.
    /// </summary>
    TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher);
}

/// <summary>The ordinary launch: the user's shell, in a repository, following the pane's size.</summary>
internal sealed class ShellLaunch : ITerminalLaunch
{
    readonly string _workingDirectory;
    readonly IPtySessionFactory _sessions;
    readonly ITerminalEngineFactory _engines;
    readonly IClipboard? _clipboard;
    readonly ITerminalPalette _palette;

    public ShellLaunch(
        string workingDirectory,
        IPtySessionFactory sessions,
        ITerminalEngineFactory engines,
        ITerminalPalette palette,
        IClipboard? clipboard = null)
    {
        _workingDirectory = workingDirectory;
        _sessions = sessions;
        _engines = engines;
        _palette = palette;
        _clipboard = clipboard;
    }

    public string Name => ShellCommand.Name;

    public TerminalSize SizeFor(TerminalSize viewport) => viewport;

    public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
        TerminalSession.Start(
            _sessions,
            _engines,
            ShellCommand.For(
                _workingDirectory,
                new PtySize(size.Columns, size.Rows),
                _palette.Resolve(TerminalColorSlot.Background)),
            dispatcher,
            clipboard: _clipboard,
            palette: _palette);
}
