namespace GitBench.Theming;

/// <summary>
/// The file browser tree's per-row foreground ramp. A directory is the one thing the reader scans
/// for in a filesystem listing, so it carries an accent icon and the brighter label; plain files
/// stay on the resting text tone and symlinks take their own tint. Ignored and hidden entries reuse
/// these colors at half alpha rather than a second set of slots.
/// </summary>
public sealed record FileBrowserRowStyles(
    uint DirectoryChevron,
    uint DirectoryIcon,
    uint DirectoryText,
    uint FileIcon,
    uint FileText,
    uint LinkIcon);

public partial record ThemeStyles
{
    private static FileBrowserRowStyles BuildFileBrowserRow(ThemePalette p, StatusPalette status) =>
        new(
            DirectoryChevron: p.TextMuted,
            DirectoryIcon: status.Info,
            DirectoryText: p.TextPrimary,
            FileIcon: p.TextMuted,
            FileText: p.TextSecondary,
            LinkIcon: status.Purple);
}
