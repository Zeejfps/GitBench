namespace GitBench.Theming;

/// <summary>
/// The narrator's spotlight over the review window's stacked diff: a translucent <see cref="Band"/>
/// across each lit row, a numbered <see cref="PinBackground"/>/<see cref="PinText"/> pin beside
/// the range's first line, and the <see cref="Wash"/> that dims the rest of a spotlit file when
/// asked. All translucent so the row tints, the +/- glyphs and the reviewer's own text selection
/// stay readable underneath — a spotlight points at code, it never replaces it.
/// </summary>
public sealed record ReviewSpotlightStyles(
    uint Band,
    uint PinBackground,
    uint PinText,
    uint Wash);

public partial record ThemeStyles
{
    private const byte SpotlightBandAlpha = 0x38;
    private const byte SpotlightWashAlpha = 0xA0;

    private static ReviewSpotlightStyles BuildReviewSpotlight(ThemePalette p) =>
        new(
            Band: WithAlpha(p.Accent, SpotlightBandAlpha),
            PinBackground: p.Accent,
            PinText: p.TextOnAccent,
            Wash: WithAlpha(p.Surface, SpotlightWashAlpha));
}
