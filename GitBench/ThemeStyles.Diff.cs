namespace GitGui;

public sealed record DiffViewStyles(
    uint PanelBackground,
    uint HeaderBackgroundIdle,
    uint HeaderBackgroundHover,
    uint HeaderBorderTop,
    uint HeaderBorderBottom,
    uint HeaderTitleIdle,
    uint HeaderTitleHover,
    uint LfsBadgeTrackedBackground,
    uint LfsBadgeTrackedText,
    uint LfsBadgeUntrackedBackground,
    uint LfsBadgeUntrackedText);

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
    uint HunkOutline,
    DiffSyntaxStyles Syntax);

// Resolved per-theme foreground colors for each non-default TokenColorSlot. TokenColorSlot is
// internal, so slot → color resolution lives in the renderer (DiffContentView); this record is
// plain color data.
public sealed record DiffSyntaxStyles(
    uint Keyword,
    uint String,
    uint Comment,
    uint Number,
    uint Type,
    uint Function,
    uint Variable,
    uint Operator,
    uint Punctuation,
    uint Constant);

public sealed record DiffHunkButtonStyles(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint Border,
    uint Text);

public partial record ThemeStyles
{
    private static DiffViewStyles BuildDiffView(ThemePalette p, StatusPalette status) =>
        new(
            PanelBackground: p.Surface,
            HeaderBackgroundIdle: p.SurfaceRaised,
            HeaderBackgroundHover: p.SurfaceHoverStrong,
            HeaderBorderTop: p.Border,
            HeaderBorderBottom: p.Border,
            HeaderTitleIdle: p.TextSubtle,
            HeaderTitleHover: p.TextStrong,
            // Tracked: a filled "info" pill — LFS storage is the expected/healthy state for a
            // binary. Untracked: a muted neutral pill — informational, not an error, so it
            // shouldn't shout the way a warning color would.
            LfsBadgeTrackedBackground: status.Info,
            LfsBadgeTrackedText: p.OnStatusText,
            LfsBadgeUntrackedBackground: p.SurfaceSunken,
            LfsBadgeUntrackedText: p.TextMuted);

    private static DiffContentStyles BuildDiffContent(ThemePalette p, StatusPalette status, DiffSyntaxPalette syntax) =>
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
            HunkOutline: p.HunkOutline,
            Syntax: new DiffSyntaxStyles(
                Keyword: syntax.Keyword,
                String: syntax.String,
                Comment: syntax.Comment,
                Number: syntax.Number,
                Type: syntax.Type,
                Function: syntax.Function,
                Variable: syntax.Variable,
                Operator: syntax.Operator,
                Punctuation: syntax.Punctuation,
                Constant: syntax.Constant));

    private static DiffHunkButtonStyles BuildDiffHunkButton(DiffHunkButtonPalette hunkButton) =>
        new(
            BackgroundIdle: hunkButton.BackgroundIdle,
            BackgroundHover: hunkButton.BackgroundHover,
            Border: hunkButton.Border,
            Text: hunkButton.Text);
}
