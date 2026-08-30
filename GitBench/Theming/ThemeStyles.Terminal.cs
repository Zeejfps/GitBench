namespace GitBench.Theming;

/// <summary>
/// The sixteen named ANSI slots a terminal addresses by index 0-15. A theme owns all sixteen
/// together, because a program that colours by slot is relying on them reading as a set against
/// that theme's surface rather than on any one of them in isolation.
/// </summary>
public sealed record AnsiColors(
    uint Black,
    uint Red,
    uint Green,
    uint Yellow,
    uint Blue,
    uint Magenta,
    uint Cyan,
    uint White,
    uint BrightBlack,
    uint BrightRed,
    uint BrightGreen,
    uint BrightYellow,
    uint BrightBlue,
    uint BrightMagenta,
    uint BrightCyan,
    uint BrightWhite);

/// <summary>
/// Everything the terminal renderer needs from the theme: the named slots an indexed colour
/// resolves through, the two colours a cell means by "default", the caret, and the rule drawn under
/// a hyperlink the pointer is over.
/// </summary>
public sealed record TerminalStyles(
    AnsiColors Ansi,
    uint DefaultForeground,
    uint DefaultBackground,
    uint Cursor,
    uint Selection,
    uint Link);

public partial record ThemeStyles
{
    private static TerminalStyles BuildTerminal(ThemePalette p, AnsiColors ansi) =>
        new(
            Ansi: ansi,
            DefaultForeground: p.TextBody,
            DefaultBackground: p.Surface,
            Cursor: p.Accent,
            Selection: p.Selection,
            Link: p.Accent);
}
