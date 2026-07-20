using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Localization;

namespace GitBench.Features.Commits;

/// <summary>
/// The Expand All / Collapse All pair a commit-changes file tree's context menus offer, so every
/// surface built on <see cref="CommitChangesPanel"/> folds the same way the local-changes panels and
/// the branches tree do. Scope follows what was right-clicked: a folder row folds its own subtree,
/// the empty space below the rows folds the whole tree.
/// </summary>
internal static class FileTreeFoldingMenu
{
    /// <summary>The whole tree, for a right-click that landed on no row.</summary>
    public static void AppendForTree(
        List<RepoBarContextMenu.Item> items,
        CommitDetailsViewModel details,
        ILocalizationService loc)
        => Append(items, details, loc, details.ExpandAllFolders, details.CollapseAllFolders);

    /// <summary>The clicked folder and everything nested under it, leaving the rest of the tree alone.</summary>
    public static void AppendForFolder(
        List<RepoBarContextMenu.Item> items,
        CommitDetailsViewModel details,
        ILocalizationService loc,
        string folderPath)
        => Append(
            items, details, loc,
            () => details.SetFolderSubtreeCollapsed(folderPath, false),
            () => details.SetFolderSubtreeCollapsed(folderPath, true));

    // Separated from whatever precedes it. A no-op in flat view, where there are no folders to fold.
    private static void Append(
        List<RepoBarContextMenu.Item> items,
        CommitDetailsViewModel details,
        ILocalizationService loc,
        Action expandAll,
        Action collapseAll)
    {
        if (details.ViewMode.Value != FileViewMode.Tree) return;
        var s = loc.Strings.Value;
        if (items.Count > 0) items.Add(RepoBarContextMenu.Separator);
        items.Add(new RepoBarContextMenu.Item(s.CommonExpandAll, expandAll, LucideIcons.ChevronDown));
        items.Add(new RepoBarContextMenu.Item(s.CommonCollapseAll, collapseAll, LucideIcons.ChevronRight));
    }
}
