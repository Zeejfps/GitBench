using GitBench.Controls;
using GitBench.Infrastructure;

namespace GitBench.Features.Branches;

/// <summary>
/// Pure flattening of a <see cref="BranchListing"/> plus collapse/expand state into the linear
/// <see cref="BranchRow"/> sequence the branches sidebar renders. Branch names containing "/" become
/// folder nodes (e.g. "feature/login" lives inside a "feature" folder). Each row carries a tree
/// <c>Depth</c> rather than pixel indent — the row widget turns depth into spacing — so this stays a
/// deterministic function of (listing, ui-state) with no layout knowledge.
/// </summary>
internal static class BranchTreeBuilder
{
    // Tree depth of each section's content. Section headers are outdented to the repo bar's group
    // indent (handled by the row widget, not depth); a section's direct content sits at depth 0 so it
    // lines up with the repo bar's primary repos, a remote's branches sit one level under their remote
    // header, and each folder level adds one more.
    private const int LocalTreeBaseDepth = 0;
    private const int RemoteHeaderDepth = 0;
    private const int RemoteTreeBaseDepth = 1;
    private const int StashDepth = 0;

    public static IReadOnlyList<BranchRow> BuildRows(BranchListing? listing, BranchesUiState ui)
    {
        var rows = new List<BranchRow>();
        if (listing == null) return rows;

        rows.Add(new LocalHeaderRow(Depth: 0, ui.LocalOpen));
        if (ui.LocalOpen)
        {
            var localTree = PathTree.Build(listing.LocalBranches, b => b.Name);
            EmitTreeRows(rows, localTree, ui, isRemote: false, remoteName: null, LocalTreeBaseDepth, depth: 0, trunkMask: 0);
        }

        rows.Add(new RemotesHeaderRow(Depth: 0, ui.RemotesOpen));
        if (ui.RemotesOpen)
        {
            foreach (var rg in listing.Remotes)
            {
                var isOpen = ui.RemoteOpen.TryGetValue(rg.Name, out var v) ? v : true;
                rows.Add(new RemoteHeaderRow(RemoteHeaderDepth, rg.Name, isOpen));
                if (!isOpen) continue;
                // The remote header is a top-level row (like a primary repo under a group header), so it
                // draws no trunk of its own; its branches start a fresh subtree just like the local one.
                var remoteTree = PathTree.Build(rg.Branches, b => b.Name);
                EmitTreeRows(rows, remoteTree, ui, isRemote: true, rg.Name, RemoteTreeBaseDepth, depth: 0, trunkMask: 0);
            }
        }

        if (listing.Stashes.Count > 0)
        {
            rows.Add(new StashesHeaderRow(Depth: 0, ui.StashesOpen));
            if (ui.StashesOpen)
            {
                foreach (var s in listing.Stashes)
                {
                    var label = $"stash@{{{s.Index}}}";
                    rows.Add(new StashRow(StashDepth, s.Index, label, s.Subject, s.Sha));
                }
            }
        }
        return rows;
    }

    // trunkMask carries the ancestors' guide state for levels [0, rowDepth-1]: the passthrough trunks
    // each row inherits, plus the parent's own column (overwritten here by each row's immediate elbow).
    private static void EmitTreeRows(List<BranchRow> rows, IReadOnlyList<PathNode<BranchEntry>> nodes, BranchesUiState ui, bool isRemote, string? remoteName, int treeBaseDepth, int depth, long trunkMask)
    {
        var rowDepth = treeBaseDepth + depth;
        var scope = isRemote ? BranchScope.Remote(remoteName!) : BranchScope.Local;
        var count = nodes.Count;
        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            var isLast = i == count - 1;
            // This row's own connector lives at its parent's column (level rowDepth-1); top-level rows
            // (rowDepth 0) sit directly under a section header and draw no connector.
            var mask = rowDepth >= 1
                ? TreeGuides.SetKind(trunkMask, rowDepth - 1, isLast ? TreeGuide.Corner : TreeGuide.Tee)
                : trunkMask;

            if (node.Leaf is { } entry)
            {
                rows.Add(isRemote
                    ? new RemoteBranchRow(rowDepth, remoteName!, entry.Name, node.Segment, entry.TipSha) { GuideMask = mask }
                    : new LocalBranchRow(rowDepth, entry.Name, node.Segment, entry.TipSha, entry.IsHead, entry.AheadBy, entry.BehindBy, entry.UpstreamState) { GuideMask = mask });
            }
            else
            {
                var folder = new BranchFolder(scope, node.FullPath);
                var open = ui.FolderOpen.TryGetValue(folder.Key, out var v) ? v : true;
                rows.Add(new FolderRow(rowDepth, folder, node.Segment, open) { GuideMask = mask });
                if (open)
                {
                    // The children's passthrough at this folder's parent column is whether the folder has
                    // a sibling below it. A top-level folder (rowDepth 0) sits under a section header, so —
                    // like a primary repo under a group header — it starts no trunk for its descendants.
                    var childTrunk = rowDepth >= 1
                        ? TreeGuides.SetKind(trunkMask, rowDepth - 1, isLast ? TreeGuide.None : TreeGuide.Through)
                        : 0L;
                    EmitTreeRows(rows, node.Children, ui, isRemote, remoteName, treeBaseDepth, depth + 1, childTrunk);
                }
            }
        }
    }

    /// Every folder path implied by a set of slash-separated branch names. A branch
    /// "feature/admin/login" contributes the folders "feature" and "feature/admin" (the
    /// final segment is the leaf branch, not a folder). The order is unspecified — callers
    /// use these only as keys into the open/closed map.
    internal static IEnumerable<string> FolderPaths(IEnumerable<string> branchNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in branchNames)
        {
            var slash = name.IndexOf('/');
            while (slash >= 0)
            {
                var prefix = name.Substring(0, slash);
                if (seen.Add(prefix)) yield return prefix;
                slash = name.IndexOf('/', slash + 1);
            }
        }
    }
}
