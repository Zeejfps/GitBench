namespace GitGui;

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

    public static IReadOnlyList<BranchRow> BuildRows(BranchListing? listing, BranchesUiState ui)
    {
        var rows = new List<BranchRow>();
        if (listing == null) return rows;

        rows.Add(new BranchRow(BranchRowKind.LocalHeader, "Local", IndentSection, ui.LocalOpen));
        if (ui.LocalOpen)
        {
            var localTree = PathTree.Build(listing.LocalBranches, b => b.Name);
            EmitTreeRows(rows, localTree, ui, isRemote: false, remoteName: null, IndentLocalTreeBase, depth: 0);
        }

        rows.Add(new BranchRow(BranchRowKind.RemotesHeader, "Remote", IndentSection, ui.RemotesOpen));
        if (ui.RemotesOpen)
        {
            foreach (var rg in listing.Remotes)
            {
                var isOpen = ui.RemoteOpen.TryGetValue(rg.Name, out var v) ? v : true;
                rows.Add(new BranchRow(BranchRowKind.RemoteHeader, rg.Name, IndentRemoteHeader, isOpen)
                {
                    RemoteName = rg.Name,
                });
                if (!isOpen) continue;
                var remoteTree = PathTree.Build(rg.Branches, b => b.Name);
                EmitTreeRows(rows, remoteTree, ui, isRemote: true, rg.Name, IndentRemoteTreeBase, depth: 0);
            }
        }

        if (listing.Stashes.Count > 0)
        {
            rows.Add(new BranchRow(BranchRowKind.StashesHeader, "Stashes", IndentSection, ui.StashesOpen));
            if (ui.StashesOpen)
            {
                foreach (var s in listing.Stashes)
                {
                    var label = $"stash@{{{s.Index}}}";
                    rows.Add(new BranchRow(BranchRowKind.Stash, s.Subject, IndentStashBase, isOpen: false)
                    {
                        TipSha = s.Sha,
                        FullPath = label,
                        StashIndex = s.Index,
                    });
                }
            }
        }
        return rows;
    }

    private static void EmitTreeRows(List<BranchRow> rows, IReadOnlyList<PathNode<BranchEntry>> nodes, BranchesUiState ui, bool isRemote, string? remoteName, float treeBase, int depth)
    {
        var indent = treeBase + depth * IndentLevel;
        foreach (var node in nodes)
        {
            if (node.Leaf is { } entry)
            {
                rows.Add(new BranchRow(isRemote ? BranchRowKind.RemoteBranch : BranchRowKind.LocalBranch, node.Segment, indent, isOpen: false)
                {
                    TipSha = entry.TipSha,
                    IsHead = entry.IsHead,
                    RemoteName = remoteName,
                    FullPath = entry.Name,
                    AheadBy = entry.AheadBy,
                    BehindBy = entry.BehindBy,
                    UpstreamState = entry.UpstreamState,
                });
            }
            else
            {
                var key = MakeFolderKey(isRemote, remoteName, node.FullPath);
                var open = ui.FolderOpen.TryGetValue(key, out var v) ? v : true;
                rows.Add(new BranchRow(BranchRowKind.Folder, node.Segment, indent, open)
                {
                    RemoteName = remoteName,
                    FullPath = node.FullPath,
                    FolderKey = key,
                });
                if (open) EmitTreeRows(rows, node.Children, ui, isRemote, remoteName, treeBase, depth + 1);
            }
        }
    }

    private static string MakeFolderKey(bool isRemote, string? remoteName, string path) =>
        isRemote ? $"remote:{remoteName}:{path}" : $"local:{path}";
}
