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
            EmitTreeRows(rows, localTree, ui, isRemote: false, remoteName: null, LocalTreeBaseDepth, depth: 0);
        }

        rows.Add(new RemotesHeaderRow(Depth: 0, ui.RemotesOpen));
        if (ui.RemotesOpen)
        {
            foreach (var rg in listing.Remotes)
            {
                var isOpen = ui.RemoteOpen.TryGetValue(rg.Name, out var v) ? v : true;
                rows.Add(new RemoteHeaderRow(RemoteHeaderDepth, rg.Name, isOpen));
                if (!isOpen) continue;
                var remoteTree = PathTree.Build(rg.Branches, b => b.Name);
                EmitTreeRows(rows, remoteTree, ui, isRemote: true, rg.Name, RemoteTreeBaseDepth, depth: 0);
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

    private static void EmitTreeRows(List<BranchRow> rows, IReadOnlyList<PathNode<BranchEntry>> nodes, BranchesUiState ui, bool isRemote, string? remoteName, int treeBaseDepth, int depth)
    {
        var rowDepth = treeBaseDepth + depth;
        var scope = isRemote ? BranchScope.Remote(remoteName!) : BranchScope.Local;
        foreach (var node in nodes)
        {
            if (node.Leaf is { } entry)
            {
                rows.Add(isRemote
                    ? new RemoteBranchRow(rowDepth, remoteName!, entry.Name, node.Segment, entry.TipSha)
                    : new LocalBranchRow(rowDepth, entry.Name, node.Segment, entry.TipSha, entry.IsHead, entry.AheadBy, entry.BehindBy, entry.UpstreamState));
            }
            else
            {
                var folder = new BranchFolder(scope, node.FullPath);
                var open = ui.FolderOpen.TryGetValue(folder.Key, out var v) ? v : true;
                rows.Add(new FolderRow(rowDepth, folder, node.Segment, open));
                if (open) EmitTreeRows(rows, node.Children, ui, isRemote, remoteName, treeBaseDepth, depth + 1);
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
