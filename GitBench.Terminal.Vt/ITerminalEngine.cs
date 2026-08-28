namespace GitBench.Terminal.Vt;

/// <summary>How a terminal engine starts up.</summary>
/// <remarks>
/// Scrollback depth is part of the contract rather than an engine's private choice, because two
/// engines with different built-in depths disagree about what a recorded session looks like.
/// </remarks>
public readonly record struct TerminalSetup(TerminalSize Size, int ScrollbackLines)
{
    public static TerminalSetup Default { get; } = new(new TerminalSize(80, 24), 1000);
}

/// <summary>
/// A VT parser and screen: bytes from a pseudo-terminal in, a grid and terminal state out.
/// </summary>
/// <remarks>
/// <para>
/// The engine is the parse at the pseudo-terminal boundary, the one place untrusted bytes become
/// domain values, so <see cref="Feed"/> never throws on malformed, truncated or partial input.
/// </para>
/// <para>
/// <see cref="Feed"/> is resumable at any byte. A pseudo-terminal hands over whatever happened to be
/// in the pipe, so an escape sequence, a UTF-8 scalar or an OSC string can be split across any
/// number of calls; an engine that only works on whole sequences is not an engine. This is the
/// clause worth testing hardest, because a suite that always feeds whole buffers will pass while
/// the real thing drops characters.
/// </para>
/// </remarks>
public interface ITerminalEngine : IDisposable
{
    /// <summary>The live screen. The same instance across calls; its contents change under the caller.</summary>
    ITerminalGrid Grid { get; }

    /// <summary>An atomic snapshot of everything observable that is not a cell.</summary>
    TerminalState State { get; }

    /// <summary>
    /// Applies <paramref name="bytes"/> to the grid. The span need not end on a sequence or UTF-8
    /// boundary; whatever is incomplete is carried into the next call.
    /// </summary>
    FeedResult Feed(ReadOnlySpan<byte> bytes);

    /// <summary>Resizes the viewport, as after a SIGWINCH.</summary>
    void Resize(TerminalSize size);
}

public static class TerminalGridExtensions
{
    /// <summary>The cell at one coordinate. Diagnostic sugar; a renderer copies whole rows.</summary>
    public static TerminalCell Cell(this ITerminalGrid grid, int column, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, grid.Size.Columns);

        var cells = new TerminalCell[grid.Size.Columns];
        grid.CopyRow(row, cells);
        return cells[column];
    }

    /// <summary>The printable text of one row with trailing blanks removed.</summary>
    public static string RowText(this ITerminalGrid grid, int row)
    {
        var cells = new TerminalCell[grid.Size.Columns];
        grid.CopyRow(row, cells);

        var text = new System.Text.StringBuilder(cells.Length);
        foreach (var cell in cells)
        {
            if (cell.Width != CellWidth.WideTrailer)
                text.Append(cell.Text);
        }

        while (text.Length > 0 && text[^1] == ' ')
            text.Length--;

        return text.ToString();
    }

    /// <summary>The whole viewport as numbered rows, for a failure message.</summary>
    public static string Describe(this ITerminalGrid grid) =>
        string.Join('\n', Enumerable.Range(0, grid.Size.Rows).Select(row => $"{row,3}|{grid.RowText(row)}|"));
}
