using ZGF.Gui.Widgets;

namespace GitBench.Theming;

public sealed record HeaderActionButtonStyles(
    uint Background,
    uint BackgroundHover,
    uint IconIdle,
    uint IconHover,
    uint IconDisabled)
{
    internal uint Surface(IInteractable s) => s.Enabled.Value && s.Hovered.Value ? BackgroundHover : Background;
    internal uint Icon(IInteractable s) => !s.Enabled.Value ? IconDisabled : (s.Hovered.Value ? IconHover : IconIdle);
}

public sealed record RepoBarStyles(
    uint Background,
    uint RightBorder);

public sealed record StatusBarStyles(
    uint Background,
    uint TopBorder,
    uint Text,
    uint Icon,
    uint IconHover,
    uint IconHoverBackground)
{
    // Icon-button glyph color: brighter while hovered.
    internal uint IconColor(IInteractable s) =>
        s.Enabled.Value && s.Hovered.Value ? IconHover : Icon;

    // Icon-button fill: transparent idle, the hover wash while hovered.
    internal uint IconButtonBackground(IInteractable s) =>
        s.Enabled.Value && s.Hovered.Value ? IconHoverBackground : 0u;
}

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
    uint Idle,
    uint Hover);

public sealed record TooltipStyles(
    uint Background,
    uint Border,
    uint Text,
    uint Shadow);

public sealed record ContextMenuStyles(
    uint Background,
    uint Border,
    uint ItemSelectedBackground,
    uint ItemActiveBackground,
    uint ItemText,
    uint ItemTextDisabled,
    uint AccentText);

public partial record ThemeStyles
{
    private static HeaderActionButtonStyles BuildHeaderActionButton(ThemePalette p) =>
        new(
            Background: 0u,
            BackgroundHover: p.SurfaceHoverStrong,
            IconIdle: p.TextSecondary,
            IconHover: p.TextStrong,
            IconDisabled: WithAlpha(p.TextSecondary, 0x66));

    private static RepoBarStyles BuildRepoBar(ThemePalette p) =>
        new(
            Background: p.Surface,
            RightBorder: p.Border);

    private static StatusBarStyles BuildStatusBar(ThemePalette p) =>
        new(
            Background: p.SurfaceMuted,
            TopBorder: p.Border,
            Text: p.TextMuted,
            Icon: p.TextSecondary,
            IconHover: p.TextStrong,
            IconHoverBackground: p.SurfaceHoverStrong);

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
            ThumbBorder: p.BorderSubtle);

    private static SidebarSplitterStyles BuildSidebarSplitter(ThemePalette p) =>
        new(
            Idle: p.Border,
            Hover: p.BorderHoverFill);

    private static HistorySplitterStyles BuildHistorySplitter(ThemePalette p) =>
        new(
            Idle: p.Border,
            Hover: p.BorderHoverFill);

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
            ItemActiveBackground: p.SurfaceSelectedSubtle,
            ItemText: p.TextSecondary,
            ItemTextDisabled: p.TextDisabled,
            AccentText: p.TextStrong);
}
