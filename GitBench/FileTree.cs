namespace GitGui;

public enum FileViewMode { Flat, Tree }

internal enum FileRowKind { Folder, File }

/// <summary>
/// One rendered row of a local-changes file list — either a directory <see cref="FileRowKind.Folder"/>
/// node or a <see cref="FileRowKind.File"/> leaf. Produced by <see cref="FileTreeBuilder"/> from the
/// flat <see cref="FileChange"/> list plus the view mode and the collapsed-folder set; the panel renders
/// these and the view model navigates/selects against the same sequence so the two never diverge.
///
/// <see cref="Files"/> is the set of descendant file paths a row operates on: a single-element list for
/// a file row, every leaf beneath a folder row. That makes "stage/discard this folder" and folder
/// selection a path-list operation the existing git ops already understand.
/// </summary>
internal sealed class FileRow
{
    private FileRow(
        FileRowKind kind,
        string displayName,
        float indent,
        bool isOpen,
        string fullPath,
        DiffSide side,
        FileChange? file,
        IReadOnlyList<string> files)
    {
        Kind = kind;
        DisplayName = displayName;
        Indent = indent;
        IsOpen = isOpen;
        FullPath = fullPath;
        Side = side;
        File = file;
        Files = files;
    }

    public FileRowKind Kind { get; }
    public string DisplayName { get; }
    public float Indent { get; }
    public bool IsOpen { get; }
    public string FullPath { get; }
    public DiffSide Side { get; }
    public FileChange? File { get; }
    public IReadOnlyList<string> Files { get; }

    public FileRowRef Ref => new(Side, FullPath, Kind == FileRowKind.Folder);

    public static FileRow ForFile(FileChange file, string displayName, float indent, DiffSide side)
        => new(FileRowKind.File, displayName, indent, isOpen: false, file.Path, side, file, new[] { file.Path });

    public static FileRow ForFolder(
        string displayName, string fullPath, float indent, bool isOpen, IReadOnlyList<string> files, DiffSide side)
        => new(FileRowKind.Folder, displayName, indent, isOpen, fullPath, side, file: null, files);
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
                flat.Add(FileRow.ForFile(f, FileChangeFormatting.FormatPath(f), 0f, side));
            return flat;
        }

        var tree = PathTree.Build(files, f => f.Path, compact: true);
        var rows = new List<FileRow>();
        EmitTreeRows(rows, tree, side, collapsed, depth: 0);
        return rows;
    }

    private static void EmitTreeRows(
        List<FileRow> rows, IReadOnlyList<PathNode<FileChange>> nodes, DiffSide side, IReadOnlySet<string> collapsed, int depth)
    {
        var indent = depth * IndentLevel;
        foreach (var node in nodes)
        {
            if (node.Leaf is { } file)
            {
                rows.Add(FileRow.ForFile(file, FileChangeFormatting.FormatLeaf(file), indent, side));
                continue;
            }

            var open = !collapsed.Contains(node.FullPath);
            var leaves = new List<string>();
            CollectLeaves(node, leaves);
            rows.Add(FileRow.ForFolder(node.Segment, node.FullPath, indent, open, leaves, side));
            if (open) EmitTreeRows(rows, node.Children, side, collapsed, depth + 1);
        }
    }

    private static void CollectLeaves(PathNode<FileChange> node, List<string> into)
    {
        if (node.Leaf is { } file) { into.Add(file.Path); return; }
        foreach (var c in node.Children) CollectLeaves(c, into);
    }
}
