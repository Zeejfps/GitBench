using ZGF.Gui.Widgets;

namespace GitBench.Theming;

public sealed record DiffViewStyles(
    uint PanelBackground,
    uint HeaderBackgroundIdle,
    uint HeaderBackgroundHover,
    uint HeaderBorderTop,
    uint HeaderBorderBottom,
    uint HeaderTitleIdle,
    uint HeaderTitleHover,
    // Tint for an engaged header toggle (e.g. the full-file view button when active).
    uint HeaderToggleActive,
    uint LfsBadgeTrackedBackground,
    uint LfsBadgeTrackedText,
    uint LfsBadgeUntrackedBackground,
    uint LfsBadgeUntrackedText)
{
    // Header icon-button glyph: the idle/hover ramp shared by the chevron, title, and buttons.
    internal uint HeaderButtonColor(IInteractable s) =>
        s.Enabled.Value && s.Hovered.Value ? HeaderTitleHover : HeaderTitleIdle;

    // The header bar's surface: tints on hover (the whole strip is the collapse toggle).
    internal uint HeaderBackground(IInteractable s) =>
        s.Hovered.Value ? HeaderBackgroundHover : HeaderBackgroundIdle;
}

public sealed record DiffContentStyles(
    uint Background,
    uint PlaceholderText,
    uint ErrorText,
    uint LineText,
    uint LineNumberText,
    uint LineAddedBackground,
    uint LineAddedEmphasisBackground,
    uint LineAddedGlyph,
    uint LineRemovedBackground,
    uint LineRemovedEmphasisBackground,
    uint LineRemovedGlyph,
    uint LineContextGlyph,
    uint SectionBackground,
    uint SectionMutedText,
    uint HunkSeparatorRangeText,
    uint HunkOutline,
    uint ExpanderIcon,
    uint ExpanderHoverBackground,
    uint ExpanderHoverIcon,
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
    uint Constant,
    // Markdown markup intents.
    uint Heading,
    uint Emphasis,
    uint Link,
    uint Code,
    uint Quote);

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
            HeaderTitleIdle: p.TextSecondary,
            HeaderTitleHover: p.TextStrong,
            HeaderToggleActive: p.Accent,
            // Tracked: a filled "info" pill — LFS storage is the expected/healthy state for a
            // binary. Untracked: a muted neutral pill — informational, not an error, so it
            // shouldn't shout the way a warning color would.
            LfsBadgeTrackedBackground: status.Info,
            LfsBadgeTrackedText: p.OnStatusText,
            LfsBadgeUntrackedBackground: p.SurfaceSunken,
            LfsBadgeUntrackedText: p.TextMuted);

    // Opacity applied to the diff's add/remove row background tints so syntax colors stay
    // legible through them. 0x00 = invisible, 0xFF = fully opaque (the pre-highlighting look).
    private const byte DiffLineTintAlpha = 0x80;

    // The changed-character box: blend the faint line tint toward the vivid status accent, then
    // draw semi-transparent over the line wash. Blending toward the accent (not just raising the
    // line tint's alpha) gains real saturation in every theme; keeping it translucent lets the
    // line tint show through so it doesn't overpower — dark's accent is far punchier than light's,
    // so a lower mix tames dark more than light, matching how vivid each theme's accent is.
    private const double DiffEmphasisMix = 0.38;
    private const byte DiffEmphasisAlpha = 0xC8;

    private static DiffContentStyles BuildDiffContent(ThemePalette p, StatusPalette status, DiffSyntaxPalette syntax) =>
        new(
            Background: p.Surface,
            PlaceholderText: p.TextMuted,
            ErrorText: status.DiffError,
            LineText: p.TextPrimary,
            LineNumberText: p.TextDim,
            // Dial the add/remove row tints back to ~50% so syntax-highlighted code reads
            // clearly through them; the full-strength +/- glyphs still signal the line kind.
            // Lower the alpha (e.g. 0x66) for a fainter tint, raise it (e.g. 0xB3) for a bolder one.
            LineAddedBackground: WithAlpha(status.SuccessLineBg, DiffLineTintAlpha),
            LineAddedEmphasisBackground: WithAlpha(Mix(status.SuccessLineBg, status.Success, DiffEmphasisMix), DiffEmphasisAlpha),
            LineAddedGlyph: status.SuccessLineGlyph,
            LineRemovedBackground: WithAlpha(status.DangerLineBg, DiffLineTintAlpha),
            LineRemovedEmphasisBackground: WithAlpha(Mix(status.DangerLineBg, status.Danger, DiffEmphasisMix), DiffEmphasisAlpha),
            LineRemovedGlyph: status.DangerLineGlyph,
            LineContextGlyph: p.TextMuted,
            SectionBackground: p.SurfaceRaised,
            SectionMutedText: p.TextSecondary,
            HunkSeparatorRangeText: p.TextMuted,
            HunkOutline: p.HunkOutline,
            // Gap expanders read as links, not gutter chrome: accent glyphs idle, a filled
            // accent chip behind the hovered one.
            ExpanderIcon: p.Accent,
            ExpanderHoverBackground: p.Accent,
            ExpanderHoverIcon: p.TextOnAccent,
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
                Constant: syntax.Constant,
                Heading: syntax.Heading,
                Emphasis: syntax.Emphasis,
                Link: syntax.Link,
                Code: syntax.Code,
                Quote: syntax.Quote));

    private static DiffHunkButtonStyles BuildDiffHunkButton(DiffHunkButtonPalette hunkButton) =>
        new(
            BackgroundIdle: hunkButton.BackgroundIdle,
            BackgroundHover: hunkButton.BackgroundHover,
            Border: hunkButton.Border,
            Text: hunkButton.Text);
}
