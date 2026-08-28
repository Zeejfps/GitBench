namespace GitBench.Terminal.Vt;

/// <summary>Which colour space a <see cref="TerminalColor"/> is expressed in.</summary>
public enum TerminalColorKind : byte
{
    /// <summary>The terminal's configured default foreground or background.</summary>
    Default = 0,

    /// <summary>An index into the 256-entry xterm palette.</summary>
    Indexed = 1,

    /// <summary>A direct 24-bit colour from SGR 38;2 or 48;2.</summary>
    Rgb = 2,
}

/// <summary>
/// The colour of one cell, as the program asked for it.
/// </summary>
/// <remarks>
/// Three cases, not a number. Only the renderer can turn "default" or a palette index into pixels,
/// because both follow the user's theme while a literal RGB must not; an engine that resolves
/// truecolor to the nearest palette slot at parse time has destroyed what the renderer needs and
/// cannot get back. The recorded corpora settle that this is not hypothetical: a real Claude Code
/// session uses 47 truecolor sequences and no palette ones at all.
/// </remarks>
public readonly record struct TerminalColor
{
    TerminalColor(TerminalColorKind kind, byte red, byte green, byte blue)
    {
        Kind = kind;
        Red = red;
        Green = green;
        Blue = blue;
    }

    public TerminalColorKind Kind { get; }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    /// <summary>The palette entry, valid only when <see cref="Kind"/> is Indexed.</summary>
    public byte Index => Red;

    public static TerminalColor Default { get; } = new(TerminalColorKind.Default, 0, 0, 0);

    public static TerminalColor Indexed(byte index) => new(TerminalColorKind.Indexed, index, 0, 0);

    public static TerminalColor Rgb(byte red, byte green, byte blue) =>
        new(TerminalColorKind.Rgb, red, green, blue);

    public override string ToString() => Kind switch
    {
        TerminalColorKind.Default => "default",
        TerminalColorKind.Indexed => $"@{Index}",
        TerminalColorKind.Rgb => $"#{Red:x2}{Green:x2}{Blue:x2}",
        _ => throw new InvalidOperationException($"Unknown colour kind {Kind}."),
    };
}
