using System.Text;

namespace GitBench.Terminal.Vt;

/// <summary>
/// The SGR attributes that survive as per-cell state.
/// </summary>
/// <remarks>Genuinely independent bits: a cell can be bold and underlined and inverse at once.</remarks>
[Flags]
public enum CellAttributes : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    CrossedOut = 1 << 7,
}

/// <summary>
/// How many columns a cell occupies.
/// </summary>
/// <remarks>
/// Three named cases rather than an integer width, so that a column counter cannot be handed a 3
/// and a renderer cannot draw a trailer by accident.
/// </remarks>
public enum CellWidth : byte
{
    /// <summary>One column, the ordinary case.</summary>
    Single = 0,

    /// <summary>The first column of a double-width character; it carries the rune.</summary>
    WideLeader = 1,

    /// <summary>The second column of a double-width character; it carries no rune of its own.</summary>
    WideTrailer = 2,
}

/// <summary>
/// One cell of the grid, as the renderer reads it.
/// </summary>
/// <remarks>
/// A value with no back-reference to the engine, so a cell stays valid and comparable after the
/// next <see cref="ITerminalEngine.Feed"/>.
/// </remarks>
public readonly record struct TerminalCell(
    Rune Rune,
    TerminalColor Foreground,
    TerminalColor Background,
    CellAttributes Attributes,
    CellWidth Width)
{
    /// <summary>
    /// The combining marks that follow <see cref="Rune"/> in this cell, or null when the cell is a
    /// single rune.
    /// </summary>
    /// <remarks>
    /// A terminal cell holds a grapheme cluster, not a codepoint, and the renderer has to place the
    /// marks over the base glyph. Keeping the base in <see cref="Rune"/> leaves the ASCII path
    /// allocation-free while an "e" followed by U+0301 still survives the grid.
    /// </remarks>
    public string? Combining { get; init; }

    /// <summary>
    /// The hyperlink this cell belongs to, or <see cref="HyperlinkId.None"/> for ordinary text.
    /// </summary>
    /// <remarks>
    /// An id and not a url, so a cell stays a value small enough to copy a row of per frame. It is
    /// also what makes a link's extent answerable without walking text: the cells of one link are
    /// the cells sharing its id, which survives the link being split across a wrap and the whole
    /// screen being reflowed. Resolve it with <see cref="ITerminalGrid.TryGetHyperlink"/>.
    /// </remarks>
    public HyperlinkId Hyperlink { get; init; }

    /// <summary>The full grapheme cluster. Allocates only when <see cref="Combining"/> is set.</summary>
    public string Text => Combining is null ? Rune.ToString() : Rune + Combining;

    /// <summary>An erased cell: a space in the default colours.</summary>
    public static TerminalCell Blank { get; } =
        new(new Rune(' '), TerminalColor.Default, TerminalColor.Default, CellAttributes.None, CellWidth.Single);

    public bool Has(CellAttributes attribute) => (Attributes & attribute) == attribute;

    public override string ToString() =>
        $"'{(Width == CellWidth.WideTrailer ? string.Empty : Text)}' fg={Foreground} bg={Background} attr={Attributes} w={Width}";
}
