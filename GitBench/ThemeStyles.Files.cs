namespace GitGui;

public sealed record LocalChangesContentStyles(
    uint ContentBackground,
    uint ColumnDivider,
    uint PlaceholderText,
    uint SplitterIdle,
    uint SplitterHover);

public sealed record SubmoduleSectionStyles(
    uint BadgeBackground,
    uint BadgeText,
    uint RowText);

public sealed record FileChangesSectionStyles(
    uint HeaderBackground,
    uint HeaderBorder,
    uint HeaderText,
    uint EmptyPlaceholderText);

public sealed record FileChangeRowStyles(
    uint RowText,
    uint RowTextActive,
    uint RowHover,
    uint RowActive,
    uint BadgeText,
    uint StatusAdded,
    uint StatusModified,
    uint StatusDeleted,
    uint StatusRenamed,
    uint StatusConflicted,
    uint StatusSubmodule,
    uint StatusOther)
{
    public uint StatusColor(FileChangeStatus status) => status switch
    {
        FileChangeStatus.Added => StatusAdded,
        FileChangeStatus.Modified => StatusModified,
        FileChangeStatus.Deleted => StatusDeleted,
        FileChangeStatus.Renamed => StatusRenamed,
        FileChangeStatus.Conflicted => StatusConflicted,
        FileChangeStatus.Submodule => StatusSubmodule,
        _ => StatusOther,
    };
}

public partial record ThemeStyles
{
    private static LocalChangesContentStyles BuildLocalChangesContent(ThemePalette p) =>
        new(
            ContentBackground: p.Surface,
            ColumnDivider: p.Border,
            PlaceholderText: p.TextMuted,
            SplitterIdle: p.Border,
            SplitterHover: p.BorderHoverFill);

    private static SubmoduleSectionStyles BuildSubmoduleSection(ThemePalette p, StatusPalette status) =>
        new(
            BadgeBackground: status.Purple,
            BadgeText: p.OnStatusText,
            RowText: p.TextMedium);

    private static FileChangesSectionStyles BuildFileChangesSection(ThemePalette p) =>
        new(
            HeaderBackground: p.SurfaceRaised,
            HeaderBorder: p.Border,
            HeaderText: p.TextMuted,
            EmptyPlaceholderText: p.TextMuted);

    private static FileChangeRowStyles BuildFileChangeRow(ThemePalette p, StatusPalette status) =>
        new(
            RowText: p.TextSecondary,
            RowTextActive: p.TextOnAccent,
            RowHover: p.SurfaceHover,
            RowActive: p.SurfaceSelected,
            BadgeText: p.OnStatusText,
            StatusAdded: status.Success,
            StatusModified: status.Warning,
            StatusDeleted: status.Danger,
            StatusRenamed: status.Info,
            StatusConflicted: status.Danger,
            StatusSubmodule: status.Purple,
            StatusOther: status.Other);
}
