namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// A cell's appearance with the rune removed — the unit a snapshot's style legend keys on.
/// </summary>
/// <remarks>
/// A snapshot-format concept rather than a seam one: the engine has no reason to know that a
/// golden writes text and colour on separate planes.
/// </remarks>
public readonly record struct CellStyle(
    TerminalColor Foreground,
    TerminalColor Background,
    CellAttributes Attributes)
{
    public static CellStyle Default { get; } =
        new(TerminalColor.Default, TerminalColor.Default, CellAttributes.None);

    public bool IsDefault => Equals(Default);
}

/// <summary>Cell and colour readings the snapshot format needs and the seam does not owe it.</summary>
public static class CellSnapshotExtensions
{
    public static CellStyle Style(this TerminalCell cell) =>
        new(cell.Foreground, cell.Background, cell.Attributes);

    public static bool IsBlank(this TerminalCell cell) => cell == TerminalCell.Blank;

    /// <summary>
    /// The order the style legend is sorted in, so a mark only moves when the set of styles moves.
    /// </summary>
    public static (byte Kind, byte Red, byte Green, byte Blue) SortKey(this TerminalColor colour) =>
        ((byte)colour.Kind, colour.Red, colour.Green, colour.Blue);
}
