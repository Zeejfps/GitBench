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
    // PLACEHOLDER (Step 5 red phase): all-zero colors so the project compiles while the tests
    // pin real per-palette values. The implementer replaces this with genuine derivations from
    // the palette (and/or literals) — both palettes must end up with non-zero, appropriately
    // differing values; see ThemeMarkdownStylesTests for the pinned contract.
    private static MarkdownStyles BuildMarkdown(ThemePalette p) =>
        new(
            Link: 0u,
            LinkHover: 0u,
            CodeChipText: 0u,
            CodeChipBackground: 0u,
            CodeBlockBackground: 0u,
            CodeBlockBorder: 0u,
            CodeBlockText: 0u,
            QuoteBar: 0u,
            QuoteText: 0u,
            Rule: 0u);
}
