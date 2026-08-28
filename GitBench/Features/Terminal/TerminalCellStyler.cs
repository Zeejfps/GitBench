using GitBench.Terminal.Vt;
using GitBench.Theming;

namespace GitBench.Features.Terminal;

/// <summary>
/// Resolves a cell's colour against one theme and spends the attributes that only change colour,
/// leaving the renderer a <see cref="RunStyle"/> it can draw without consulting the theme again.
/// </summary>
/// <remarks>
/// Immutable and safe to share across panes and threads; a theme change means constructing a new
/// one rather than mutating this. Bold picks a face rather than a brighter palette slot, so an
/// indexed colour resolves the same whether or not the cell is bold.
/// </remarks>
internal sealed class TerminalCellStyler : ICellStyler
{
    private readonly TerminalStyles _styles;

    public TerminalCellStyler(TerminalStyles styles) => _styles = styles;

    public RunStyle Style(in TerminalCell cell)
    {
        var foreground = Resolve(cell.Foreground, _styles.DefaultForeground);
        var background = Resolve(cell.Background, _styles.DefaultBackground);

        if (cell.Has(CellAttributes.Inverse))
            (foreground, background) = (background, foreground);

        // Toward the background this run is actually painted on, which is what makes dim mean
        // something: the foreground always ends up between where it was and what it sits on, so
        // dimming can only ever reduce contrast. Mixing toward the theme's background instead would
        // make dim a no-op on an inverse cell, and darken it on a light one.
        if (cell.Has(CellAttributes.Dim))
            foreground = Midpoint(foreground, background);

        if (cell.Has(CellAttributes.Hidden))
            foreground = background;

        return new RunStyle(
            Foreground: foreground,
            Background: background,
            Bold: cell.Has(CellAttributes.Bold),
            Italic: cell.Has(CellAttributes.Italic),
            Underline: cell.Has(CellAttributes.Underline),
            StrikeThrough: cell.Has(CellAttributes.CrossedOut));
    }

    private uint Resolve(in TerminalColor color, uint themeDefault) => Opaque(color.Kind switch
    {
        TerminalColorKind.Default => themeDefault,
        TerminalColorKind.Indexed => Indexed(color.Index),
        TerminalColorKind.Rgb => Pack(color.Red, color.Green, color.Blue),
        _ => throw new InvalidOperationException($"Unknown colour kind {color.Kind}."),
    });

    private uint Indexed(byte index)
    {
        var ansi = _styles.Ansi;
        return index switch
        {
            0 => ansi.Black,
            1 => ansi.Red,
            2 => ansi.Green,
            3 => ansi.Yellow,
            4 => ansi.Blue,
            5 => ansi.Magenta,
            6 => ansi.Cyan,
            7 => ansi.White,
            8 => ansi.BrightBlack,
            9 => ansi.BrightRed,
            10 => ansi.BrightGreen,
            11 => ansi.BrightYellow,
            12 => ansi.BrightBlue,
            13 => ansi.BrightMagenta,
            14 => ansi.BrightCyan,
            15 => ansi.BrightWhite,
            < 232 => Cube(index),
            _ => Greyscale(index),
        };
    }

    private static uint Cube(byte index)
    {
        var n = index - 16u;
        return Pack(CubeComponent(n / 36u), CubeComponent(n / 6u % 6u), CubeComponent(n % 6u));
    }

    private static uint CubeComponent(uint step) => step == 0u ? 0u : 55u + (40u * step);

    private static uint Greyscale(byte index)
    {
        var level = 8u + (10u * (index - 232u));
        return Pack(level, level, level);
    }

    private static uint Midpoint(uint from, uint to) => Pack(
        Halfway(Channel(from, 16), Channel(to, 16)),
        Halfway(Channel(from, 8), Channel(to, 8)),
        Halfway(Channel(from, 0), Channel(to, 0)));

    // Rounds half away from zero, unlike ThemeStyles.Mix, whose Math.Round is banker's: a dim
    // channel must never land below its own midpoint or dimmed text creeps darker each theme.
    private static uint Halfway(uint a, uint b) => (a + b + 1u) / 2u;

    private static uint Channel(uint argb, int shift) => (argb >> shift) & 0xFFu;

    private static uint Pack(uint red, uint green, uint blue) =>
        0xFF000000u | (red << 16) | (green << 8) | blue;

    private static uint Opaque(uint argb) => 0xFF000000u | (argb & 0x00FFFFFFu);
}
