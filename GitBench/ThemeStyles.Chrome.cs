namespace GitGui;

public sealed record HeaderActionButtonStyles(
    uint Background,
    uint BackgroundHover,
    uint IconIdle,
    uint IconHover,
    uint IconDisabled);

public sealed record RepoBarStyles(
    uint Background,
    uint RightBorder);

public sealed record ActionsToolbarStyles(
    uint Background,
    uint BorderBottom,
    uint BadgeAhead,
    uint BadgeBehind);

public sealed record ScrollBarStyles(
    uint TrackBackground,
    uint TrackBorder,
    uint ThumbIdleBackground,
    uint ThumbHoverBackground,
    uint ThumbBorder);

public sealed record SidebarSplitterStyles(
    uint Idle,
    uint Hover);

public sealed record HistorySplitterStyles(
    uint HoverFill,
    uint HoverLine);

public sealed record TooltipStyles(
    uint Background,
    uint Border,
    uint Text,
    uint Shadow);

public sealed record ContextMenuStyles(
    uint Background,
    uint Border,
    uint ItemSelectedBackground,
    uint ItemText,
    uint ItemTextDisabled,
    uint AccentText);

public partial record ThemeStyles
{
    private static HeaderActionButtonStyles BuildHeaderActionButton(ThemePalette p) =>
        new(
            Background: 0u,
            BackgroundHover: p.SurfaceHoverStrong,
            IconIdle: p.TextMedium,
            IconHover: p.TextStrong,
            IconDisabled: WithAlpha(p.TextMedium, 0x66));

    private static RepoBarStyles BuildRepoBar(ThemePalette p) =>
        new(
            Background: p.Surface,
            RightBorder: p.Border);

    private static ActionsToolbarStyles BuildActionsToolbar(ThemePalette p, StatusPalette status) =>
        new(
            Background: p.Surface,
            BorderBottom: p.Border,
            BadgeAhead: status.SuccessSoft,
            BadgeBehind: status.WarningSoft);

    private static ScrollBarStyles BuildScrollBar(ThemePalette p) =>
        new(
            TrackBackground: p.ScrollBarTrackBg,
            TrackBorder: p.Border,
            ThumbIdleBackground: p.BorderMuted,
            ThumbHoverBackground: p.BorderMutedHover,
            ThumbBorder: p.ScrollBarThumbBorder);

    private static SidebarSplitterStyles BuildSidebarSplitter(ThemePalette p) =>
        new(
            Idle: p.Border,
            Hover: p.BorderHoverFill);

    private static HistorySplitterStyles BuildHistorySplitter(ThemePalette p) =>
        new(
            HoverFill: p.BorderHoverFill,
            HoverLine: p.BorderHoverLine);

    private static TooltipStyles BuildTooltip(ThemePalette p, TooltipPalette tooltip) =>
        new(
            Background: tooltip.Background,
            Border: tooltip.Border,
            Text: tooltip.Text,
            Shadow: p.Shadow);

    private static ContextMenuStyles BuildContextMenu(ThemePalette p) =>
        new(
            Background: p.Surface,
            Border: p.Border,
            ItemSelectedBackground: p.SurfaceHover,
            ItemText: p.TextSecondary,
            ItemTextDisabled: p.TextDisabled,
            AccentText: p.TextStrong);
}
