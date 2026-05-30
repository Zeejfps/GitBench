namespace GitGui;

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
    uint TextMedium,
    uint TextMuted,
    uint TextDim,
    uint TextDisabled,
    uint TextOnAccent,
    uint Shadow,
    uint BarSurface,
    uint InputSurface,
    uint InputSurfaceHover,
    uint TextEmphasis,
    uint TextSubtle,
    uint TextFaint,
    uint OnStatusText,
    uint RowSubtleText,
    uint HunkOutline,
    uint Selection,
    uint Placeholder,
    uint DialogHeaderSeparator,
    uint CheckboxBorderIdle,
    uint CheckboxDisabledFill,
    uint SegmentActiveBg,
    uint ScrollBarTrackBg,
    uint ScrollBarThumbBorder,
    uint OperationRowHoverBg,
    uint CommitRowSelectedBg,
    uint CommitRowSelectedText);

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

public sealed record CommitBadgePalette(
    uint LocalBg,
    uint RemoteBg,
    uint HeadBg,
    uint Text);
