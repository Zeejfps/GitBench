using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Infrastructure;

namespace GitBench.Features.Branches;

/// <summary>
/// Pure flattening of a <see cref="BranchListing"/> plus collapse/expand state and the repo's
/// <see cref="RepoStatus"/> into the linear <see cref="BranchRow"/> sequence the branches sidebar
/// renders. Branch names containing "/" become folder nodes (e.g. "feature/login" lives inside a
/// "feature" folder). Each row carries a tree <c>Depth</c> rather than pixel indent — the row widget
/// turns depth into spacing — so this stays a deterministic function of its inputs with no layout
/// knowledge.
///
/// This is also the one site where a branch row's ahead/behind is decided: HEAD's comes from the
/// status store, every other local branch's from the listing.
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

    public static IReadOnlyList<BranchRow> BuildRows(BranchListing? listing, BranchesUiState ui, RepoStatus headStatus)
    {
        var rows = new List<BranchRow>();
        if (listing == null) return rows;

        rows.Add(new LocalHeaderRow(Depth: 0, ui.LocalOpen));
        if (ui.LocalOpen)
        {
            var localTree = PathTree.Build(listing.LocalBranches, b => b.Name);
            EmitTreeRows(rows, localTree, ui, BranchScope.Local, LocalTreeBaseDepth, depth: 0, trunkMask: 0,
                leafRow: (entry, segment, rowDepth, mask) => new LocalBranchRow(
                    rowDepth, entry.Name, segment, entry.TipSha,
                    IsHead: entry is LocalBranchEntry.Head,
                    Upstream: UpstreamKindOf(entry),
                    Sync: SyncFor(entry, headStatus)) { GuideMask = mask });
        }

        rows.Add(new RemotesHeaderRow(Depth: 0, ui.RemotesOpen));
        if (ui.RemotesOpen)
        {
            var remoteCount = listing.Remotes.Count;
            for (var ri = 0; ri < remoteCount; ri++)
            {
                var rg = listing.Remotes[ri];
                var isOpen = ui.RemoteOpen.TryGetValue(rg.Name, out var v) ? v : true;
                // The remote header is a child of the REMOTES section: its own connector hangs off the
                // root column, and its branches inherit the root trunk while another remote follows below.
                var isLast = ri == remoteCount - 1;
                var headerMask = TreeGuides.SetKind(0, RemoteHeaderDepth, isLast ? TreeGuide.Corner : TreeGuide.Tee);
                rows.Add(new RemoteHeaderRow(RemoteHeaderDepth, rg.Name, isOpen) { GuideMask = headerMask });
                if (!isOpen) continue;
                var childTrunk = TreeGuides.SetKind(0, RemoteHeaderDepth, isLast ? TreeGuide.None : TreeGuide.Through);
                var remoteTree = PathTree.Build(rg.Branches, b => b.Name);
                EmitTreeRows(rows, remoteTree, ui, BranchScope.Remote(rg.Name), RemoteTreeBaseDepth, depth: 0, childTrunk,
                    leafRow: (entry, segment, rowDepth, mask) =>
                        new RemoteBranchRow(rowDepth, rg.Name, entry.Name, segment, entry.TipSha) { GuideMask = mask });
            }
        }

        if (listing.Stashes.Count > 0)
        {
            rows.Add(new StashesHeaderRow(Depth: 0, ui.StashesOpen));
            if (ui.StashesOpen)
            {
                var stashCount = listing.Stashes.Count;
                for (var si = 0; si < stashCount; si++)
                {
                    var s = listing.Stashes[si];
                    var label = $"stash@{{{s.Index}}}";
                    var mask = TreeGuides.SetKind(0, StashDepth, si == stashCount - 1 ? TreeGuide.Corner : TreeGuide.Tee);
                    rows.Add(new StashRow(StashDepth, s.Index, label, s.Subject, s.Sha) { GuideMask = mask });
                }
            }
        }
        return rows;
    }

    // HEAD's counts come from the status store — the same git read that drives the toolbar — so the
    // badge and the push/pull buttons can never disagree. Every other local branch keeps for-each-ref,
    // which is the only thing that can report them.
    private static BranchSync? SyncFor(LocalBranchEntry entry, RepoStatus headStatus) => entry switch
    {
        LocalBranchEntry.Head => headStatus is { HasUpstream: true, IsDetached: false }
            ? new BranchSync(headStatus.Ahead, headStatus.Behind)
            : null,
        LocalBranchEntry.Other o => (o.Upstream as LocalUpstream.Tracked)?.Sync,
        _ => null,
    };

    private static BranchUpstreamKind UpstreamKindOf(LocalBranchEntry entry) => entry switch
    {
        LocalBranchEntry.Head { Upstream: HeadUpstreamState.Tracked } => BranchUpstreamKind.Tracked,
        LocalBranchEntry.Head { Upstream: HeadUpstreamState.Gone } => BranchUpstreamKind.Gone,
        LocalBranchEntry.Other { Upstream: LocalUpstream.Tracked } => BranchUpstreamKind.Tracked,
        LocalBranchEntry.Other { Upstream: LocalUpstream.Gone } => BranchUpstreamKind.Gone,
        _ => BranchUpstreamKind.None,
    };

    // trunkMask carries the ancestors' passthrough trunks for guide levels [0, rowDepth-1] — level 0 is
    // the section/remote header (the root), each deeper level a tree depth in. Each row sets its own
    // connector at level rowDepth (its parent's column) on top of those.
    private static void EmitTreeRows<TLeaf>(
        List<BranchRow> rows, IReadOnlyList<PathNode<TLeaf>> nodes, BranchesUiState ui, BranchScope scope,
        int treeBaseDepth, int depth, long trunkMask, Func<TLeaf, string, int, long, BranchRow> leafRow)
        where TLeaf : class
    {
        var rowDepth = treeBaseDepth + depth;
        var count = nodes.Count;
        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            var isLast = i == count - 1;
            var mask = TreeGuides.SetKind(trunkMask, rowDepth, isLast ? TreeGuide.Corner : TreeGuide.Tee);

            if (node.Leaf is { } entry)
            {
                rows.Add(leafRow(entry, node.Segment, rowDepth, mask));
            }
            else
            {
                var folder = new BranchFolder(scope, node.FullPath);
                var open = ui.FolderOpen.TryGetValue(folder.Key, out var v) ? v : true;
                rows.Add(new FolderRow(rowDepth, folder, node.Segment, open) { GuideMask = mask });
                if (open)
                {
                    // The folder's children inherit its trunk at its own column — a passthrough while the
                    // folder has a sibling below it, nothing once it is the last.
                    var childTrunk = TreeGuides.SetKind(trunkMask, rowDepth, isLast ? TreeGuide.None : TreeGuide.Through);
                    EmitTreeRows(rows, node.Children, ui, scope, treeBaseDepth, depth + 1, childTrunk, leafRow);
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
