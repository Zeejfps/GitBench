namespace GitGui;

public sealed record DiffViewStyles(
    uint PanelBackground,
    uint HeaderBackgroundIdle,
    uint HeaderBackgroundHover,
    uint HeaderBorderTop,
    uint HeaderBorderBottom,
    uint HeaderTitleIdle,
    uint HeaderTitleHover);

public sealed record DiffContentStyles(
    uint Background,
    uint PlaceholderText,
    uint ErrorText,
    uint LineText,
    uint LineNumberText,
    uint LineAddedBackground,
    uint LineAddedGlyph,
    uint LineRemovedBackground,
    uint LineRemovedGlyph,
    uint LineContextGlyph,
    uint SectionBackground,
    uint SectionMutedText,
    uint HunkSeparatorRangeText,
    uint HunkOutline);

public sealed record DiffHunkButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint Border,
    uint Text);

public partial record ThemeStyles
{
    private static DiffViewStyles BuildDiffView(ThemePalette p) =>
        new(
            PanelBackground: p.Surface,
            HeaderBackgroundIdle: p.SurfaceRaised,
            HeaderBackgroundHover: p.SurfaceHoverStrong,
            HeaderBorderTop: p.Border,
            HeaderBorderBottom: p.Border,
            HeaderTitleIdle: p.TextSubtle,
            HeaderTitleHover: p.TextStrong);

    private static DiffContentStyles BuildDiffContent(ThemePalette p, StatusPalette status) =>
        new(
            Background: p.Surface,
            PlaceholderText: p.TextMuted,
            ErrorText: status.DiffError,
            LineText: p.TextEmphasis,
            LineNumberText: p.TextFaint,
            LineAddedBackground: status.SuccessLineBg,
            LineAddedGlyph: status.SuccessLineGlyph,
            LineRemovedBackground: status.DangerLineBg,
            LineRemovedGlyph: status.DangerLineGlyph,
            LineContextGlyph: p.TextMuted,
            SectionBackground: p.SurfaceRaised,
            SectionMutedText: p.TextSecondary,
            HunkSeparatorRangeText: p.TextMuted,
            HunkOutline: p.HunkOutline);

    private static DiffHunkButtonStyles BuildDiffHunkButton(DiffHunkButtonPalette hunkButton) =>
        new(
            BackgroundIdle: hunkButton.BackgroundIdle,
            BackgroundHover: hunkButton.BackgroundHover,
            Border: hunkButton.Border,
            Text: hunkButton.Text);
}
