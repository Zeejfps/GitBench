namespace GitGui;

public sealed record CommitBarStyles(
    uint Background,
    uint TopBorder);

public sealed record ModeSwitcherStyles(
    uint PillBorder,
    uint SegmentSeparator,
    uint SegmentIdleBackground,
    uint SegmentHoverBackground,
    uint SegmentActiveBackground,
    uint SegmentIdleText,
    uint SegmentHoverText,
    uint SegmentActiveText);

public sealed record CommitDetailsViewStyles(
    uint Background,
    uint BorderLeft,
    uint PrimaryText,
    uint SecondaryText,
    uint MutedText,
    uint PlaceholderText,
    uint SplitterIdle,
    uint SplitterHover);

public sealed record CommitsViewStyles(
    uint Background,
    uint HeaderBackground,
    uint HeaderBorderBottom,
    uint HeaderText,
    uint RowText,
    uint RowTextActive,
    uint RowTextDim,
    uint RowSelectedBackground,
    uint PlaceholderText,
    uint ColumnDividerIdle,
    uint ColumnDividerHoverFill,
    uint ColumnDividerHoverLine,
    uint BadgeLocalBackground,
    uint BadgeRemoteBackground,
    uint BadgeHeadBackground,
    uint BadgeText);

public partial record ThemeStyles
{
    private static CommitBarStyles BuildCommitBar(ThemePalette p) =>
        new(
            Background: p.BarSurface,
            TopBorder: p.Border);

    private static ModeSwitcherStyles BuildModeSwitcher(ThemePalette p) =>
        new(
            PillBorder: p.BorderStrong,
            SegmentSeparator: p.BorderStrong,
            SegmentIdleBackground: 0u,
            SegmentHoverBackground: p.InputSurfaceHover,
            SegmentActiveBackground: p.SegmentActiveBg,
            SegmentIdleText: p.TextSecondary,
            SegmentHoverText: p.TextStrong,
            SegmentActiveText: p.TextOnAccent);

    private static CommitDetailsViewStyles BuildCommitDetailsView(ThemePalette p) =>
        new(
            Background: p.SurfaceSunken,
            BorderLeft: p.Border,
            PrimaryText: p.TextEmphasis,
            SecondaryText: p.TextSecondary,
            MutedText: p.TextFaint,
            PlaceholderText: p.TextMuted,
            SplitterIdle: p.Border,
            SplitterHover: p.BorderHoverFill);

    private static CommitsViewStyles BuildCommitsView(ThemePalette p, CommitBadgePalette badge) =>
        new(
            Background: p.Surface,
            HeaderBackground: p.BarSurface,
            HeaderBorderBottom: p.Border,
            HeaderText: p.TextMuted,
            RowText: p.TextSecondary,
            RowTextActive: p.CommitRowSelectedText,
            RowTextDim: p.TextDim,
            RowSelectedBackground: p.CommitRowSelectedBg,
            PlaceholderText: p.TextMuted,
            ColumnDividerIdle: p.Border,
            ColumnDividerHoverFill: p.BorderHoverFill,
            ColumnDividerHoverLine: p.BorderHoverLine,
            BadgeLocalBackground: badge.LocalBg,
            BadgeRemoteBackground: badge.RemoteBg,
            BadgeHeadBackground: badge.HeadBg,
            BadgeText: badge.Text);
}
