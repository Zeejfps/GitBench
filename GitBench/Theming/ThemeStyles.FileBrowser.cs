using GitBench.Features.FileBrowser;

namespace GitBench.Theming;

// Curated icon tints for the file browser's file kinds, one per non-default FileKind. Chosen per
// theme, and chosen to avoid the two hues the tree has already spent: blue is a directory and
// purple is a symlink, so the kinds take green, amber, teal, pink and grey.
public sealed record FileKindPalette(
    uint Code,
    uint Data,
    uint Docs,
    uint Media,
    uint Binary);

/// <summary>
/// The file browser tree's per-row foreground ramp. A directory is the one thing the reader scans
/// for in a filesystem listing, so it carries an accent icon and the brighter label; plain files
/// stay on the resting text tone and symlinks take their own tint. Only icons carry a kind tint —
/// the names stay on one ramp, or a directory listing turns into a color chart. Ignored and hidden
/// entries reuse these colors at half alpha rather than a second set of slots.
/// </summary>
public sealed record FileBrowserRowStyles(
    uint DirectoryChevron,
    uint DirectoryIcon,
    uint DirectoryText,
    uint FileIcon,
    uint FileText,
    uint LinkIcon,
    FileKindPalette Kinds)
{
    internal uint IconFor(FileKind kind) => kind switch
    {
        FileKind.Code => Kinds.Code,
        FileKind.Data => Kinds.Data,
        FileKind.Docs => Kinds.Docs,
        FileKind.Media => Kinds.Media,
        FileKind.Binary => Kinds.Binary,
        _ => FileIcon,
    };
}

public partial record ThemeStyles
{
    private static FileBrowserRowStyles BuildFileBrowserRow(
        ThemePalette p,
        StatusPalette status,
        FileKindPalette kinds) =>
        new(
            DirectoryChevron: p.TextMuted,
            DirectoryIcon: status.Info,
            DirectoryText: p.TextPrimary,
            FileIcon: p.TextMuted,
            FileText: p.TextSecondary,
            LinkIcon: status.Purple,
            Kinds: kinds);
}
