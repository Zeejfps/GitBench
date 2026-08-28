namespace GitBench.Terminal.Vt;

/// <summary>
/// Everything outside the engine may read about the screen.
/// </summary>
/// <remarks>
/// <para>
/// Row 0 is the top of the live viewport whatever the scroll state, and rows above it are addressed
/// with negative indices down to <c>-ScrollbackRows</c>. One coordinate system for viewport and
/// history means a cursor row and a grid row are the same kind of number, and a row reference does
/// not change meaning as history grows.
/// </para>
/// <para>
/// There is deliberately no scroll position here. Where the user has scrolled to is the renderer's,
/// and putting it in the engine would make the same grid read differently depending on UI state.
/// </para>
/// <para>
/// Cells are copied out rather than handed over as a span, because a span would fix every engine's
/// internal storage to this exact layout. A row-sized copy per row per frame is nothing beside
/// drawing it, and it is what lets a second engine store cells however it likes.
/// </para>
/// </remarks>
public interface ITerminalGrid
{
    TerminalSize Size { get; }

    /// <summary>
    /// How many lines of history sit above the viewport and can be read at negative row indices.
    /// </summary>
    int ScrollbackRows { get; }

    /// <summary>
    /// Copies one row into <paramref name="destination"/>, which must be at least
    /// <see cref="TerminalSize.Columns"/> long.
    /// </summary>
    /// <param name="row">A row from <c>-ScrollbackRows</c> to <c>Size.Rows - 1</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row is off the grid.</exception>
    void CopyRow(int row, Span<TerminalCell> destination);

    /// <summary>
    /// True when this row is the continuation of one that ran past the right margin, so that
    /// copying a selection joins it to the row above instead of inserting a newline.
    /// </summary>
    /// <remarks>
    /// Stated as "continues the row above" rather than "wraps into the row below" because only this
    /// direction is always answerable: whether the bottom viewport row has wrapped is unknown until
    /// the next rune arrives and scrolls it. It is also the direction every xterm.js-derived engine
    /// already stores, so an adapter does not have to translate.
    /// </remarks>
    bool ContinuesPreviousRow(int row);
}
