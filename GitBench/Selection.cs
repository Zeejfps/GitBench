namespace GitGui;

/// <summary>
/// Local-changes selection state, owned by <see cref="LocalChangesViewModel"/> and
/// rendered by both the row panels (highlight) and the diff view (target).
///
/// Files and folders are independent selectable rows: <see cref="Rows"/> holds exactly
/// what the user selected (a folder is its own item — selecting a file never implies its
/// folder, and vice-versa). <see cref="Items"/> is the derived set of file leaves those
/// rows resolve to (a folder row expands to every file beneath it), and is what staging,
/// discard, and the diff pane consume.
///
/// <see cref="Create"/> is the only constructor; it drops any row or anchor that no longer
/// resolves to a file in the corresponding side's list, so an external reload (watcher
/// tick, refs change, terminal git op) can't strand the selection. The all-same-side
/// property holds by construction at every call site (clicks arrive one row at a time;
/// ops always move paths to a single side), so the type doesn't re-check it at runtime.
/// </summary>
internal sealed class Selection
{
    /// <summary>The selected rows — files and folders, exactly as picked.</summary>
    public IReadOnlyList<FileRowRef> Rows { get; }

    /// <summary>The file leaves <see cref="Rows"/> resolves to (folder rows → descendants).</summary>
    public IReadOnlyList<DiffTarget> Items { get; }

    public FileRowRef? Anchor { get; }
    public FileRowRef? Cursor { get; }

    private readonly HashSet<FileRowRef> _rowSet;
    private readonly HashSet<DiffTarget> _itemSet;

    public static readonly Selection Empty = new(Array.Empty<FileRowRef>(), Array.Empty<DiffTarget>(), null, null);

    public int Count => Rows.Count;

    /// <summary>The side the selection lives on, or null when empty.</summary>
    public DiffSide? Side => Rows.Count > 0 ? Rows[0].Side : null;

    /// <summary>
    /// The single selected file target, or null when the selection is empty, multi, or a
    /// folder (a folder selection has no single diff target).
    /// </summary>
    public DiffTarget? Single => Rows.Count == 1 && !Rows[0].IsFolder && Items.Count == 1 ? Items[0] : null;

    private Selection(
        IReadOnlyList<FileRowRef> rows,
        IReadOnlyList<DiffTarget> items,
        FileRowRef? anchor,
        FileRowRef? cursor)
    {
        Rows = rows;
        Items = items;
        Anchor = anchor;
        Cursor = cursor;
        _rowSet = new HashSet<FileRowRef>(rows);
        _itemSet = new HashSet<DiffTarget>(items);
    }

    /// <summary>True when the given row is itself selected (drives row highlight).</summary>
    public bool ContainsRow(FileRowRef row) => _rowSet.Contains(row);

    /// <summary>True when the given file leaf is in the resolved selection.</summary>
    public bool Contains(string path, DiffSide side)
        => _itemSet.Contains(new DiffTarget(path, side));

    /// <summary>
    /// Resolved file paths on <paramref name="side"/>, in selection order. Empty when the
    /// selection lives on the other side.
    /// </summary>
    public IReadOnlyList<string> PathsOn(DiffSide side)
        => Items.Count > 0 && Items[0].Side == side
            ? Items.Select(t => t.Path).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Builds a selection from the given rows, pruning rows and anchor/cursor that no
    /// longer resolve to a file, and expanding folder rows to their descendant files.
    /// </summary>
    public static Selection Create(
        IReadOnlyList<FileRowRef> rows,
        FileRowRef? anchor,
        FileRowRef? cursor,
        IReadOnlyList<FileChange> unstaged,
        IReadOnlyList<FileChange> staged)
    {
        if (rows.Count == 0 && anchor == null && cursor == null) return Empty;

        var unstagedPaths = BuildPathSet(unstaged);
        var stagedPaths = BuildPathSet(staged);

        var prunedRows = new List<FileRowRef>(rows.Count);
        foreach (var r in rows)
            if (RefValid(r, unstagedPaths, stagedPaths)) prunedRows.Add(r);

        var items = ExpandToFiles(prunedRows, unstaged, staged);

        var normalizedAnchor = NormalizeRef(anchor, unstagedPaths, stagedPaths);
        var normalizedCursor = NormalizeRef(cursor, unstagedPaths, stagedPaths);

        return prunedRows.Count == 0 && normalizedAnchor == null && normalizedCursor == null
            ? Empty
            : new Selection(prunedRows, items, normalizedAnchor, normalizedCursor);
    }

    /// <summary>
    /// Builds a selection from a flat list of file paths landing on a single side (each
    /// becomes its own file row). Used by stage/unstage to place the post-op selection on
    /// the destination side.
    /// </summary>
    public static Selection FromPaths(
        IReadOnlyList<string> paths,
        DiffSide side,
        IReadOnlyList<FileChange> unstaged,
        IReadOnlyList<FileChange> staged)
    {
        if (paths.Count == 0) return Empty;
        var rows = new List<FileRowRef>(paths.Count);
        foreach (var p in paths) rows.Add(new FileRowRef(side, p, IsFolder: false));
        return Create(rows, rows[0], rows[0], unstaged, staged);
    }

    // Expands rows to the file leaves they cover, in row order, deduped (a folder and one
    // of its descendant files can both be selected).
    private static IReadOnlyList<DiffTarget> ExpandToFiles(
        IReadOnlyList<FileRowRef> rows, IReadOnlyList<FileChange> unstaged, IReadOnlyList<FileChange> staged)
    {
        if (rows.Count == 0) return Array.Empty<DiffTarget>();
        var seen = new HashSet<string>();
        var items = new List<DiffTarget>();
        foreach (var r in rows)
        {
            if (!r.IsFolder)
            {
                if (seen.Add(r.FullPath)) items.Add(new DiffTarget(r.FullPath, r.Side));
                continue;
            }
            var list = r.Side == DiffSide.Unstaged ? unstaged : staged;
            var prefix = r.FullPath + "/";
            foreach (var f in list)
                if (f.Path.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(f.Path))
                    items.Add(new DiffTarget(f.Path, r.Side));
        }
        return items;
    }

    // A file ref is valid while its path is present; a folder ref while any file still
    // sits beneath its prefix (so it isn't stranded by an unrelated file leaving).
    private static bool RefValid(FileRowRef r, HashSet<string> unstaged, HashSet<string> staged)
    {
        var available = r.Side == DiffSide.Unstaged ? unstaged : staged;
        if (!r.IsFolder) return available.Contains(r.FullPath);
        var prefix = r.FullPath + "/";
        foreach (var p in available)
            if (p.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static FileRowRef? NormalizeRef(FileRowRef? @ref, HashSet<string> unstaged, HashSet<string> staged)
        => @ref is { } r && RefValid(r, unstaged, staged) ? r : null;

    private static HashSet<string> BuildPathSet(IReadOnlyList<FileChange> files)
    {
        var set = new HashSet<string>(files.Count);
        foreach (var f in files) set.Add(f.Path);
        return set;
    }
}
