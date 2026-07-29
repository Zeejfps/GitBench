namespace GitBench.Theming;

/// <summary>
/// Theme slots for the markdown renderer (<c>MarkdownWidget</c> and friends). All slots are ARGB
/// colors; text-bearing slots (<see cref="Link"/>, <see cref="CodeChipText"/>,
/// <see cref="CodeBlockText"/>, <see cref="QuoteText"/>) must be fully opaque. Syntax-highlight
/// colors inside code blocks are NOT here — code blocks reuse the existing per-theme
/// <see cref="DiffSyntaxStyles"/> (<c>ThemeStyles.DiffContent.Syntax</c>) so markdown code and
/// diff code always agree. Heading sizes are likewise not themed: they come from the fixed
/// <c>FontSize</c> ladder (see <c>MarkdownWidgetTests</c>).
/// </summary>
public sealed record MarkdownStyles(
    // Inline link text color and the color a hovered link switches to.
    uint Link,
    uint LinkHover,
    // Inline `code` chip: foreground text and the rounded chip background behind it.
    uint CodeChipText,
    uint CodeChipBackground,
    // Fenced code block: box fill, box border, and the plain (un-highlighted) code text color.
    uint CodeBlockBackground,
    uint CodeBlockBorder,
    uint CodeBlockText,
    // Blockquote: the leading accent bar and the quoted body text color.
    uint QuoteBar,
    uint QuoteText,
    // Thematic break rule color.
    uint Rule);

public partial record ThemeStyles
{
    // Derived from the palette like the other Build* methods, so both modes stay coherent with
    // the surrounding chrome: links ride the accent ramp; code surfaces reuse the sunken/muted
    // surface ladder with the standard border; quotes read as secondary prose behind a muted bar.
    private static MarkdownStyles BuildMarkdown(ThemePalette p) =>
        new(
            Link: p.Accent,
            LinkHover: p.AccentHover,
            CodeChipText: p.TextPrimary,
            CodeChipBackground: p.SurfaceMuted,
            CodeBlockBackground: p.SurfaceSunken,
            CodeBlockBorder: p.Border,
            CodeBlockText: p.TextPrimary,
            QuoteBar: p.BorderMuted,
            QuoteText: p.TextSecondary,
            Rule: p.Border);
}
