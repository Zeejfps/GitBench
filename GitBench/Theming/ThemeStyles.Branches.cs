using GitBench.Git;

namespace GitBench.Theming;

public sealed record BranchesHeaderStyles(
    uint Background,
    uint BorderBottom,
    uint PrefixText,
    uint ActiveText,
    uint DetachedText);

public sealed record GroupHeaderRowStyles(
    uint ChevronText,
    uint BackgroundIdle,
    uint BackgroundHover,
    uint NameText)
{
    public uint Background(bool hovered) => hovered ? BackgroundHover : BackgroundIdle;
}

public sealed record GroupRenameFieldStyles(
    uint Background,
    uint Border,
    uint Text,
    uint Caret,
    uint Selection);

public sealed record RepoBarRowStyles(
    uint TextIdle,
    uint TextActive,
    uint TextMissing,
    uint IconAccentPrimary,
    uint IconAccentWorktree,
    uint IconAccentSubmodule,
    uint BadgeError,
    uint BadgeDirty)
{
    public uint Text(bool active, bool missing)
        => missing ? TextMissing : active ? TextActive : TextIdle;

    // Row icon: each kind carries its own accent at rest; on the active fill or a missing checkout
    // the icon follows the label ramp so it stays legible.
    public uint Icon(RepoKind kind, bool active, bool missing)
    {
        if (kind == RepoKind.Primary)
            return active || missing ? Text(active, missing) : IconAccentPrimary;
        if (missing) return TextMissing;
        return kind == RepoKind.Worktree ? IconAccentWorktree : IconAccentSubmodule;
    }
}

public sealed record BranchesViewStyles(
    uint ViewBackground,
    uint RowText,
    uint HeadIdleText,
    uint RowTextDim,
    uint SectionHeaderText,
    uint AheadColor,
    uint BehindColor);

public sealed record BranchPreviewStyles(
    uint Clean,
    uint Conflict);

public partial record ThemeStyles
{
    private static BranchesHeaderStyles BuildBranchesHeader(ThemePalette p) =>
        new(
            Background: p.Surface,
            BorderBottom: p.Border,
            PrefixText: p.TextMuted,
            ActiveText: p.TextStrong,
            DetachedText: p.TextDisabled);

    private static GroupHeaderRowStyles BuildGroupHeaderRow(ThemePalette p) =>
        new(
            ChevronText: p.TextMuted,
            BackgroundIdle: 0u,
            BackgroundHover: p.SurfaceHover,
            NameText: p.TextMuted);

    private static GroupRenameFieldStyles BuildGroupRenameField(ThemePalette p) =>
        new(
            Background: p.InputSurface,
            Border: p.Accent,
            Text: p.TextPrimary,
            Caret: p.TextPrimary,
            Selection: p.Selection);

    private static RepoBarRowStyles BuildRepoBarRow(ThemePalette p, StatusPalette status) =>
        new(
            TextIdle: p.TextSecondary,
            TextActive: p.RowSubtleText,
            TextMissing: p.TextDisabled,
            IconAccentPrimary: p.Accent,
            IconAccentWorktree: status.Info,
            IconAccentSubmodule: status.Purple,
            BadgeError: status.Danger,
            BadgeDirty: status.Warning);

    private static BranchesViewStyles BuildBranchesView(ThemePalette p, StatusPalette status) =>
        new(
            ViewBackground: p.Surface,
            RowText: p.TextSecondary,
            HeadIdleText: p.TextStrong,
            RowTextDim: p.TextDisabled,
            SectionHeaderText: p.TextMuted,
            AheadColor: status.SuccessSoft,
            BehindColor: status.WarningSoft);

    private static BranchPreviewStyles BuildBranchPreview(StatusPalette status) =>
        new(
            Clean: status.SuccessSoft,
            Conflict: status.WarningSoft);
}
