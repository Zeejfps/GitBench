using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Git;
using GitBench.Infrastructure;

namespace GitBench.Features.LocalChanges;

public enum FileViewMode { Flat, Tree }

/// <summary>
/// One rendered row of a local-changes file list — a directory <see cref="Folder"/> node or a
/// <see cref="File"/> leaf. Produced by <see cref="FileTreeBuilder"/> from the flat
/// <see cref="FileChange"/> list plus the view mode and the collapsed-folder set; the panel renders
/// these and the view model navigates/selects against the same sequence so the two never diverge.
///
/// <see cref="Files"/> is the set of descendant file paths a row operates on: a single-element list for
/// a file row, every leaf beneath a folder row. That makes "stage/discard this folder" and folder
/// selection a path-list operation the existing git ops already understand.
/// </summary>
internal abstract class FileRow
{
    private FileRow(string displayName, float indent, string fullPath, DiffSide side, IReadOnlyList<string> files, TreeGuides guides)
    {
        DisplayName = displayName;
        Indent = indent;
        FullPath = fullPath;
        Side = side;
        Files = files;
        Guides = guides;
    }

    public string DisplayName { get; }
    public float Indent { get; }
    public string FullPath { get; }
    public DiffSide Side { get; }
    public IReadOnlyList<string> Files { get; }

    // The ancestry connectors drawn behind the row in tree mode (trunks + the elbow into it).
    // Empty for flat mode and top-level rows — they are the tree's roots.
    public TreeGuides Guides { get; }

    public FileRowRef Ref => new(Side, FullPath, this is Folder);

    public sealed class File : FileRow
    {
        public FileChange Change { get; }

        public File(FileChange change, string displayName, float indent, DiffSide side, TreeGuides guides = default)
            : base(displayName, indent, change.Path, side, [change.Path], guides)
            => Change = change;
    }

    public sealed class Folder : FileRow
    {
        public bool IsOpen { get; }

        public Folder(
            string displayName, string fullPath, float indent, bool isOpen, IReadOnlyList<string> files, DiffSide side,
            TreeGuides guides = default)
            : base(displayName, indent, fullPath, side, files, guides)
            => IsOpen = isOpen;
    }
}

/// <summary>
/// Stable identity for a row across rebuilds — a folder's full path or a file's path, scoped to a side.
/// Used as the selection anchor/cursor so keyboard navigation survives the row list being rebuilt
/// (collapse toggle, view-mode switch, working-tree reload).
/// </summary>
internal readonly record struct FileRowRef(DiffSide Side, string FullPath, bool IsFolder);

/// <summary>
/// Pure flattening of a <see cref="FileChange"/> list plus (view-mode, collapsed-folders) into the linear
/// <see cref="FileRow"/> sequence a <c>LocalChangesPanel</c> renders. Flat mode emits one file row per
/// file; tree mode builds a <see cref="PathTree"/> over the file paths (with single-child folder
/// compaction, e.g. <c>Assets/Scripts/UI</c>) and emits a row per node, hiding rows under collapsed
/// folders. No dependency on layout pixels or the view tree, so both the panel (render) and the view
/// model (navigation) can call it.
/// </summary>
internal static class FileTreeBuilder
{
    // Shared with the other tree views. File rows reserve the same chevron column as
    // folder rows (see FileChangesUI.DrawFileRow), so a child file's icon lands one full
    // level to the right of its parent folder's icon — matching BranchesView's nesting.
    public const float IndentLevel = TreeMetrics.IndentLevel;

    private static readonly IReadOnlyList<FileRow> Empty = Array.Empty<FileRow>();

    public static IReadOnlyList<FileRow> BuildRows(
        IReadOnlyList<FileChange> files,
        DiffSide side,
        FileViewMode mode,
        IReadOnlySet<string> collapsed)
    {
        if (files.Count == 0) return Empty;

        if (mode == FileViewMode.Flat)
        {
            var flat = new List<FileRow>(files.Count);
            foreach (var f in files)
                flat.Add(new FileRow.File(f, FileChangeFormatting.FormatPath(f), 0f, side));
            return flat;
        }

        var tree = PathTree.Build(files, f => f.Path, compact: true);
        var rows = new List<FileRow>();
        EmitTreeRows(rows, tree, side, collapsed, depth: 0, trunkMask: 0);
        return rows;
    }

    // trunkMask carries the ancestors' passthrough trunks for guide levels [1, depth-1]; each row sets
    // its own connector at level depth (its parent folder's chevron column) on top of those. Top-level
    // rows are the tree's roots — there is no in-list header to hang off, so they carry no guides and
    // level 0 (the section-header column the widget trees use) stays unset.
    private static void EmitTreeRows(
        List<FileRow> rows, IReadOnlyList<PathNode<FileChange>> nodes, DiffSide side, IReadOnlySet<string> collapsed, int depth, long trunkMask)
    {
        var indent = depth * IndentLevel;
        var count = nodes.Count;
        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            var isLast = i == count - 1;
            var guides = depth == 0
                ? default
                : new TreeGuides(TreeGuides.SetKind(trunkMask, depth, isLast ? TreeGuide.Corner : TreeGuide.Tee), depth + 1);

            if (node.Leaf is { } file)
            {
                rows.Add(new FileRow.File(file, FileChangeFormatting.FormatLeaf(file), indent, side, guides));
                continue;
            }

            var open = !collapsed.Contains(node.FullPath);
            var leaves = new List<string>();
            CollectLeaves(node, leaves);
            rows.Add(new FileRow.Folder(node.Segment, node.FullPath, indent, open, leaves, side, guides));
            if (open)
            {
                // The folder's children inherit its trunk at its own column — a passthrough while the
                // folder has a sibling below it, nothing once it is the last.
                var childTrunk = depth == 0
                    ? 0L
                    : TreeGuides.SetKind(trunkMask, depth, isLast ? TreeGuide.None : TreeGuide.Through);
                EmitTreeRows(rows, node.Children, side, collapsed, depth + 1, childTrunk);
            }
        }
    }

    private static void CollectLeaves(PathNode<FileChange> node, List<string> into)
    {
        if (node.Leaf is { } file) { into.Add(file.Path); return; }
        foreach (var c in node.Children) CollectLeaves(c, into);
    }
}
