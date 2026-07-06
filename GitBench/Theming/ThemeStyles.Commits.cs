namespace GitBench.Theming;

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
    uint SegmentActiveText)
{
    public uint SegmentBackground(bool active, bool hovered)
        => active ? SegmentActiveBackground : hovered ? SegmentHoverBackground : SegmentIdleBackground;

    public uint SegmentText(bool active, bool hovered)
        => active ? SegmentActiveText : hovered ? SegmentHoverText : SegmentIdleText;
}

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
    uint RowTextDim,
    uint PlaceholderText,
    uint ColumnDividerIdle,
    uint ColumnDividerHoverFill,
    uint ColumnDividerHoverLine,
    uint BadgeLocalBackground,
    uint BadgeRemoteBackground,
    uint BadgeHeadBackground,
    uint BadgeTagBackground,
    uint BadgeText,
    uint BadgeBranchInSyncIcon,
    uint BadgeBranchDivergedIcon,
    uint BadgeBranchUntrackedIcon,
    // Tint for the search bar's remote filter toggle while it's hiding remote-only branches.
    uint FilterToggleActive);

public partial record ThemeStyles
{
    private static CommitBarStyles BuildCommitBar(ThemePalette p) =>
        new(
            Background: p.SurfaceMuted,
            TopBorder: p.Border);

    private static ModeSwitcherStyles BuildModeSwitcher(ThemePalette p) =>
        new(
            PillBorder: p.BorderStrong,
            SegmentSeparator: p.BorderStrong,
            SegmentIdleBackground: 0u,
            SegmentHoverBackground: p.InputSurfaceHover,
            SegmentActiveBackground: p.SurfaceSelected,
            SegmentIdleText: p.TextSecondary,
            SegmentHoverText: p.TextStrong,
            SegmentActiveText: p.TextOnAccent);

    private static CommitDetailsViewStyles BuildCommitDetailsView(ThemePalette p) =>
        new(
            Background: p.SurfaceSunken,
            BorderLeft: p.Border,
            PrimaryText: p.TextPrimary,
            SecondaryText: p.TextSecondary,
            MutedText: p.TextDim,
            PlaceholderText: p.TextMuted,
            SplitterIdle: p.Border,
            SplitterHover: p.BorderHoverFill);

    private static CommitsViewStyles BuildCommitsView(ThemePalette p, CommitBadgePalette badge) =>
        new(
            Background: p.Surface,
            HeaderBackground: p.SurfaceMuted,
            HeaderBorderBottom: p.Border,
            HeaderText: p.TextMuted,
            RowText: p.TextSecondary,
            RowTextDim: p.TextDim,
            PlaceholderText: p.TextMuted,
            ColumnDividerIdle: p.Border,
            ColumnDividerHoverFill: p.BorderHoverFill,
            ColumnDividerHoverLine: p.BorderHoverLine,
            BadgeLocalBackground: badge.LocalBg,
            BadgeRemoteBackground: badge.RemoteBg,
            BadgeHeadBackground: badge.HeadBg,
            BadgeTagBackground: badge.TagBg,
            BadgeText: badge.Text,
            BadgeBranchInSyncIcon: badge.BranchInSyncIcon,
            BadgeBranchDivergedIcon: badge.BranchDivergedIcon,
            BadgeBranchUntrackedIcon: badge.BranchUntrackedIcon,
            FilterToggleActive: p.Accent);
}
