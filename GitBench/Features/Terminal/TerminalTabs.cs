using ZGF.Observable;

namespace GitBench.Features.Terminal;

/// <summary>
/// One repository's terminals, and which of them is on screen.
/// </summary>
/// <remarks>
/// <para>
/// A tab is a terminal and a terminal is still a repository's, so this is the value behind a repo id
/// in <see cref="ITerminalSessionStore"/> rather than a list of its own with the repository as a
/// column: a shell's working directory is the repo root, and a tab that outlived its repository
/// would be sitting in a directory that may have been pruned.
/// </para>
/// <para>
/// There may be none. A repository is not handed a terminal it never asked for: a terminal exists
/// because someone pressed for one, and pressing for one starts a shell. So there is no such thing
/// here as an idle terminal waiting to be started — <see cref="Active"/> is null while the list is
/// empty, and closing the last tab leaves it that way rather than putting an unstarted one back.
/// </para>
/// </remarks>
internal sealed class TerminalTabs : IDisposable
{
    readonly Func<TerminalInstance> _create;
    readonly ObservableList<TerminalInstance> _terminals = new();
    readonly State<TerminalInstance?> _active = new(null);

    bool _disposed;

    public TerminalTabs(Func<TerminalInstance> create) => _create = create;

    /// <summary>The tabs, in strip order. Mutated only through this class.</summary>
    public ObservableList<TerminalInstance> Terminals => _terminals;

    /// <summary>The terminal the pane draws, or null while this repository has none.</summary>
    public IReadable<TerminalInstance?> Active => _active;

    /// <summary>Whether any of these terminals is holding a shell process.</summary>
    /// <remarks>
    /// Over the whole list rather than the active one, so the quit confirmation is right about a
    /// repository whose live shell is in a tab that is merely not on screen.
    /// </remarks>
    public bool HasLiveShell
    {
        get
        {
            foreach (var terminal in _terminals)
                if (terminal.HasLiveShell) return true;
            return false;
        }
    }

    /// <summary>
    /// The whole of what asking for a terminal means: one is made, put on screen, and its shell
    /// started. There is no step between — a terminal nobody has started is a tab naming a shell
    /// that does not exist.
    /// </summary>
    /// <remarks>
    /// The spawn itself still waits for the new grid to report a viewport, since a shell has to be
    /// told how big it is. That is the only window in which one of these is not yet running.
    /// </remarks>
    public TerminalInstance StartNew() => StartNew(_create());

    /// <summary>Like <see cref="StartNew()"/>, for a terminal made with a launch of its own.</summary>
    public TerminalInstance StartNew(TerminalInstance terminal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _terminals.Add(terminal);
        _active.Value = terminal;
        terminal.Start();
        return terminal;
    }

    /// <summary>Puts an existing terminal on screen. A no-op for one that is not (or no longer) here.</summary>
    public void Activate(TerminalInstance terminal)
    {
        if (_disposed || _terminals.IndexOf(terminal) < 0) return;

        _active.Value = terminal;
    }

    /// <summary>
    /// Ends a terminal and takes its tab off the strip. The neighbour takes its place when it was
    /// the one on screen; closing the last one leaves the repository with none.
    /// </summary>
    /// <remarks>
    /// By identity rather than by index, because a close is asked for and answered at two different
    /// times: the confirmation is modal to the window but this list is not frozen while it is up, and
    /// a repository can close or a shell exit in between. A terminal that has since gone is a no-op
    /// here, not an off-by-one that ends the wrong shell.
    /// </remarks>
    public void Close(TerminalInstance terminal)
    {
        if (_disposed) return;

        var index = _terminals.IndexOf(terminal);
        if (index < 0) return;

        _terminals.RemoveAt(index);

        // Reassigned before disposal so nothing is left drawing a screen whose session has gone.
        if (ReferenceEquals(_active.Value, terminal))
            _active.Value = _terminals.Count == 0 ? null : _terminals[Math.Min(index, _terminals.Count - 1)];

        terminal.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var terminal in _terminals) terminal.Dispose();
        _terminals.Clear();
        _active.Dispose();
    }
}
