using GitBench.Features.LocalChanges;
using GitBench.Infrastructure;
using GitBench.Localization;

namespace GitBench.Features.Branches;

/// <summary>
/// Pure flattening of a <see cref="BranchListing"/> plus collapse/expand state into the
/// linear <see cref="BranchRow"/> sequence that <see cref="BranchesView"/> renders.
/// Branch names containing "/" become folder nodes (e.g. "feature/login" lives inside
/// a "feature" folder). Lives outside the view because none of this depends on layout
/// pixels, the canvas, or the view tree — it's a deterministic function of
/// (listing, ui-state).
/// </summary>
internal static class BranchTreeBuilder
{
    // Section headers ("Local" / "Remote" / "Stashes") and the per-row indent math are
    // render concerns the VM produces no input for beyond the raw listing and the
    // open/closed flags, so the constants live with the builder rather than the VM.
    private const float IndentLevel = TreeMetrics.IndentLevel;
    private const float IndentSection = 0f;                  // section header (Local / Remote / Stashes)
    private const float IndentRemoteHeader = IndentLevel;    // one level under the Remote section
    private const float IndentLocalTreeBase = IndentLevel;   // local branches: one level under their section
    private const float IndentRemoteTreeBase = IndentLevel * 2; // remote branches: under section → remote
    private const float IndentStashBase = IndentLevel;       // stashes: one level under their section

    public static IReadOnlyList<BranchRow> BuildRows(BranchListing? listing, BranchesUiState ui, Strings strings)
    {
        var rows = new List<BranchRow>();
        if (listing == null) return rows;

        rows.Add(new LocalHeaderRow(ui.LocalOpen, IndentSection, strings.BranchesSectionLocal));
        if (ui.LocalOpen)
        {
            var localTree = PathTree.Build(listing.LocalBranches, b => b.Name);
            EmitTreeRows(rows, localTree, ui, isRemote: false, remoteName: null, IndentLocalTreeBase, depth: 0);
        }

        rows.Add(new RemotesHeaderRow(ui.RemotesOpen, IndentSection, strings.BranchesSectionRemote));
        if (ui.RemotesOpen)
        {
            foreach (var rg in listing.Remotes)
            {
                var isOpen = ui.RemoteOpen.TryGetValue(rg.Name, out var v) ? v : true;
                rows.Add(new RemoteHeaderRow(rg.Name, isOpen, IndentRemoteHeader, rg.Name));
                if (!isOpen) continue;
                var remoteTree = PathTree.Build(rg.Branches, b => b.Name);
                EmitTreeRows(rows, remoteTree, ui, isRemote: true, rg.Name, IndentRemoteTreeBase, depth: 0);
            }
        }

        if (listing.Stashes.Count > 0)
        {
            rows.Add(new StashesHeaderRow(ui.StashesOpen, IndentSection, strings.BranchesSectionStashes));
            if (ui.StashesOpen)
            {
                foreach (var s in listing.Stashes)
                {
                    var label = $"stash@{{{s.Index}}}";
                    rows.Add(new StashRow(s.Index, label, s.Sha, IndentStashBase, s.Subject));
                }
            }
        }
        return rows;
    }

    private static void EmitTreeRows(List<BranchRow> rows, IReadOnlyList<PathNode<BranchEntry>> nodes, BranchesUiState ui, bool isRemote, string? remoteName, float treeBase, int depth)
    {
        var indent = treeBase + depth * IndentLevel;
        var scope = isRemote ? BranchScope.Remote(remoteName!) : BranchScope.Local;
        foreach (var node in nodes)
        {
            if (node.Leaf is { } entry)
            {
                rows.Add(isRemote
                    ? new RemoteBranchRow(remoteName!, entry.Name, entry.TipSha, indent, node.Segment)
                    : new LocalBranchRow(entry.Name, entry.TipSha, entry.IsHead, entry.AheadBy, entry.BehindBy, entry.UpstreamState, indent, node.Segment));
            }
            else
            {
                var folder = new BranchFolder(scope, node.FullPath);
                var open = ui.FolderOpen.TryGetValue(folder.Key, out var v) ? v : true;
                rows.Add(new FolderRow(folder, open, indent, node.Segment));
                if (open) EmitTreeRows(rows, node.Children, ui, isRemote, remoteName, treeBase, depth + 1);
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
