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
/// There is always at least one, which is why <see cref="Active"/> is not nullable: a repository
/// with a terminal pane and no terminal in it is a state the pane would have to draw something for
/// and the user would have no way back out of. Closing the last tab therefore does not empty the
/// list — it puts a fresh idle terminal in its place, which is the state the repository opened in,
/// so the strip goes away and the offer to start a shell comes back.
/// </para>
/// </remarks>
internal sealed class TerminalTabs : IDisposable
{
    readonly Func<TerminalInstance> _create;
    readonly ObservableList<TerminalInstance> _terminals = new();
    readonly State<TerminalInstance> _active;

    bool _disposed;

    public TerminalTabs(Func<TerminalInstance> create)
    {
        _create = create;

        var first = _create();
        _terminals.Add(first);
        _active = new State<TerminalInstance>(first);
    }

    /// <summary>The tabs, in strip order. Mutated only through this class.</summary>
    public ObservableList<TerminalInstance> Terminals => _terminals;

    /// <summary>The terminal the pane draws.</summary>
    public IReadable<TerminalInstance> Active => _active;

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
    /// Whether any of these terminals has been asked for a shell — including one whose shell has
    /// since exited or failed, since what it left is still on screen.
    /// </summary>
    /// <remarks>
    /// A repository nobody has started a terminal in has nothing worth naming, which is what the
    /// pane reads to decide whether there is a strip to draw at all.
    /// </remarks>
    public bool AnyStarted
    {
        get
        {
            foreach (var terminal in _terminals)
                if (terminal.Render.Value is not TerminalRenderState.Idle) return true;
            return false;
        }
    }

    /// <summary>Adds a terminal and puts it on screen. Idle: making one starts no shell.</summary>
    public TerminalInstance Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var terminal = _create();
        _terminals.Add(terminal);
        _active.Value = terminal;
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
    /// the one on screen; closing the last one leaves a fresh idle terminal instead of nothing.
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

        // Never empty: the repository keeps a terminal, unstarted, exactly as it was handed one when
        // it was first activated. Nothing here knows that this makes the strip disappear — that
        // follows from AnyStarted, which is the same question asked of the same list.
        if (_terminals.Count == 0) _terminals.Add(_create());

        // Reassigned before disposal so nothing is left drawing a screen whose session has gone.
        if (ReferenceEquals(_active.Value, terminal))
            _active.Value = _terminals[Math.Min(index, _terminals.Count - 1)];

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
