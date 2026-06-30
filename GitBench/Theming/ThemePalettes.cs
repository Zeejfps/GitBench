namespace GitBench.Theming;

public sealed record ThemePalette(
    uint Surface,
    uint SurfaceRaised,
    uint SurfaceSunken,
    uint SurfaceMuted,
    uint SurfaceHover,
    uint SurfaceHoverStrong,
    uint SurfaceSelected,
    uint SurfaceSelectedSubtle,
    uint Border,
    uint BorderSubtle,
    uint BorderStrong,
    uint BorderMuted,
    uint BorderMutedHover,
    uint BorderHoverFill,
    uint BorderHoverLine,
    uint Accent,
    uint AccentHover,
    uint TextStrong,
    uint TextPrimary,
    uint TextBody,
    uint TextSecondary,
    uint TextMuted,
    uint TextDim,
    uint TextDisabled,
    uint TextOnAccent,
    uint Shadow,
    uint InputSurface,
    uint InputSurfaceHover,
    uint OnStatusText,
    uint RowSubtleText,
    uint HunkOutline,
    uint Selection,
    uint Placeholder,
    uint CheckboxBorderIdle,
    uint CheckboxDisabledFill,
    uint ScrollBarTrackBg);

public sealed record StatusPalette(
    uint Success,
    uint Warning,
    uint Danger,
    uint Info,
    uint Purple,
    uint SuccessSoft,
    uint WarningSoft,
    uint SuccessBar,
    uint SuccessText,
    uint SuccessLineBg,
    uint SuccessLineGlyph,
    uint DangerBar,
    uint DangerText,
    uint DangerLineBg,
    uint DangerLineGlyph,
    uint Other,
    uint DialogError,
    uint DialogWarning,
    uint DiffError);

public sealed record BannerStyles(
    uint Background,
    uint Border,
    uint Text);

public sealed record TooltipPalette(
    uint Background,
    uint Border,
    uint Text);

public sealed record DiffHunkButtonPalette(
    uint BackgroundIdle,
    uint BackgroundHover,
    uint Border,
    uint Text);

// Curated foreground colors for diff syntax highlighting, one per non-default TokenColorSlot.
// Chosen per theme to read as native editor colors and stay legible over the diff's
// add/remove background tints (which are foreground-independent).
public sealed record DiffSyntaxPalette(
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

public sealed record CommitBadgePalette(
    uint LocalBg,
    uint RemoteBg,
    uint HeadBg,
    uint TagBg,
    uint Text,
    uint BranchInSyncIcon,
    uint BranchDivergedIcon,
    uint BranchUntrackedIcon);
