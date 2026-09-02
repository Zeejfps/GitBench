using GitBench.Controls;
using GitBench.Features.CodeIntel;
using GitBench.Infrastructure;

namespace GitBench.Features.FileBrowser;

/// <summary>
/// The file browser's model: which directories are open, what each of them holds, and the flat row
/// sequence that falls out of the two. Lists lazily, one directory at a time, and caches what it
/// listed until something tells it the working tree moved.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe, and not reentrant: every method reads the disk through <see cref="IFileSystemReader"/>
/// and mutates the cache, so one owner drives it from one lane at a time. The pane never touches it —
/// the store owns it, works it on a background task, and publishes <see cref="Rows"/> to the UI.
/// </para>
/// <para>
/// Flattening reconciles the expanded set against what is actually on disk: a directory that was
/// open and has since been deleted or renamed loses its entry, and so does one whose expansion the
/// walk refuses (a link back up its own path, or a directory past the depth cap). Entries under a
/// directory the walk never listed are left alone — nothing was learned about them.
/// </para>
/// <para>
/// A file expands too, into the declarations inside it, which costs a read and a parse of that one
/// file. Those go through the <c>outline</c> delegate the owner supplies — the tree knows what a
/// declaration is shaped like and nothing about how one is found — and are cached beside the
/// directory listings, so they are re-read on exactly the same events.
/// </para>
/// <para>
/// A declaration that declares something itself opens too, but the other way round from a
/// directory: opening the file shows the whole outline, and what is tracked is the set the reader
/// has explicitly <em>closed</em>. Anything else would make reading a file's shape a drill-down —
/// three clicks to reach a method — and would need state for every node to say the ordinary thing.
/// The closed set is keyed by containment chain, so it survives a re-parse, and it is not persisted.
/// </para>
/// </remarks>
internal sealed class FileBrowserTree
{
    /// <summary>How many levels of rows the tree will emit below the root. A link may be listed
    /// safely without being safely walkable, and the cap is the backstop for the cases the
    /// canonical-ancestor test cannot see.</summary>
    public const int DefaultMaxDepth = 24;

    private static readonly IReadOnlySet<string> NoIgnored = new HashSet<string>(StringComparer.Ordinal);

    private readonly IFileSystemReader _files;
    private readonly IIgnoreOracle _ignore;
    private readonly int _maxDepth;
    private readonly Func<string, CancellationToken, FileOutline?>? _outline;
    private readonly Dictionary<string, Cached> _cache = new(PathKey.Comparer);
    private readonly Dictionary<string, FileOutline?> _outlines = new(PathKey.Comparer);
    private readonly HashSet<string> _expanded = new(PathKey.Comparer);
    private readonly HashSet<string> _closedSymbols = new(StringComparer.Ordinal);

    private IReadOnlyList<FileBrowserRow> _rows = [];

    public FileBrowserTree(
        IFileSystemReader files,
        IIgnoreOracle ignore,
        string rootPath,
        Func<string, CancellationToken, FileOutline?>? outline = null,
        int maxDepth = DefaultMaxDepth)
    {
        _files = files;
        _ignore = ignore;
        _outline = outline;
        _maxDepth = Math.Max(1, maxDepth);
        RootPath = PathKey.Normalize(rootPath);
        Flatten(CancellationToken.None);
    }

    public string RootPath { get; }

    public IReadOnlyList<FileBrowserRow> Rows => _rows;

    /// <summary>
    /// Whether entries the ignore rules match, and entries the OS marks hidden, are listed at all.
    /// They are by default and deliberately: dropping them would reproduce the blind spot this
    /// feature exists to fix. The toggle is for the moment a build directory is in the way.
    /// </summary>
    public bool ShowHidden { get; private set; } = true;

    /// <summary>The open directories, absolute, for persisting across a restart.</summary>
    public IReadOnlyCollection<string> ExpandedPaths => _expanded;

    public bool IsExpanded(string absolutePath) => _expanded.Contains(absolutePath);

    public void Expand(string absolutePath, CancellationToken cancellation = default)
    {
        if (!_expanded.Add(absolutePath)) return;
        Flatten(cancellation);
    }

    public void Collapse(string absolutePath, CancellationToken cancellation = default)
    {
        if (!_expanded.Remove(absolutePath)) return;
        Flatten(cancellation);
    }

    public void Toggle(string absolutePath, CancellationToken cancellation = default)
    {
        if (_expanded.Contains(absolutePath)) Collapse(absolutePath, cancellation);
        else Expand(absolutePath, cancellation);
    }

    /// <summary>Opens or closes one declaration, by the <see cref="FileBrowserRow.RowKey"/> of the
    /// row that names it.</summary>
    public void ToggleSymbol(string rowKey, CancellationToken cancellation = default)
    {
        if (!_closedSymbols.Remove(rowKey)) _closedSymbols.Add(rowKey);
        Flatten(cancellation);
    }

    public void SetShowHidden(bool show, CancellationToken cancellation = default)
    {
        if (ShowHidden == show) return;
        ShowHidden = show;
        Flatten(cancellation);
    }

    public void Reveal(string absolutePath, CancellationToken cancellation = default)
    {
        var changed = false;
        var current = Path.GetDirectoryName(PathKey.Normalize(absolutePath));
        var ancestors = new List<string>();
        while (current is { Length: > 0 } && !PathKey.Comparer.Equals(current, RootPath))
        {
            ancestors.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length == current.Length) return;
            current = parent;
        }

        if (current is not { Length: > 0 }) return;

        foreach (var ancestor in ancestors) changed |= _expanded.Add(ancestor);
        if (changed) Flatten(cancellation);
    }

    /// <summary>Adopts a persisted set of open directories in one pass, rather than one flatten per
    /// path.</summary>
    public void RestoreExpanded(IEnumerable<string> absolutePaths, CancellationToken cancellation = default)
    {
        var changed = false;
        foreach (var path in absolutePaths) changed |= _expanded.Add(path);
        if (changed) Flatten(cancellation);
    }

    /// <summary>
    /// Drops every cached listing and reads the open directories again, keeping the expanded set and
    /// whatever of it still exists. This is what a working-tree change costs, so it is bounded by
    /// what the reader has opened rather than by what is on disk.
    /// </summary>
    public void Refresh(CancellationToken cancellation = default)
    {
        _cache.Clear();
        _outlines.Clear();
        Flatten(cancellation);
    }

    private void Flatten(CancellationToken cancellation)
    {
        var rows = new List<FileBrowserRow>();
        var chain = new HashSet<string>(PathKey.Comparer) { Canonical(RootPath) };
        Emit(rows, RootPath, depth: 0, parentIgnored: false, trunkMask: 0, chain, cancellation);
        _rows = rows;
    }

    private void Emit(
        List<FileBrowserRow> rows,
        string directory,
        int depth,
        bool parentIgnored,
        long trunkMask,
        HashSet<string> chain,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        var cached = Listing(directory, parentIgnored, cancellation);
        if (cached.Listing is not DirectoryListing.Listed listed) return;

        var entries = Ordered(listed.Entries);
        ReconcileExpanded(directory, entries);

        var visible = new List<FileSystemEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var ignored = parentIgnored || cached.Ignored.Contains(Relative(directory, entry));
            if (!ShowHidden && (ignored || entry.IsHidden)) continue;
            visible.Add(entry);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var entry = visible[i];
            var isLast = i == visible.Count - 1;
            var full = Path.Combine(directory, entry.Name);
            var ignored = parentIgnored || cached.Ignored.Contains(Relative(directory, entry));
            var guides = depth == 0
                ? default
                : new TreeGuides(
                    TreeGuides.SetKind(trunkMask, depth, isLast ? TreeGuide.Corner : TreeGuide.Tee),
                    depth + 1);

            if (!entry.IsDirectory)
            {
                var expandable = _outline != null && CodeLanguages.Detect(entry.Name) != null;
                var showing = expandable && _expanded.Contains(full);
                if (!expandable) _expanded.Remove(full);

                rows.Add(new FileBrowserRow.File(
                    full, entry.Name, depth, ignored, entry.IsHidden, entry.IsLink, guides, expandable, showing));

                if (showing)
                {
                    EmitSymbols(
                        rows, full, depth + 1, ignored, entry.IsHidden,
                        ChildTrunk(depth, trunkMask, isLast), cancellation);
                }
                continue;
            }

            var wanted = _expanded.Contains(full);
            var canonical = full;
            var open = wanted && CanDescend(full, entry, depth, chain, out canonical);
            if (wanted && !open) _expanded.Remove(full);

            rows.Add(new FileBrowserRow.Directory(
                full, entry.Name, depth, ignored, entry.IsHidden, entry.IsLink, guides, open));

            if (!open) continue;

            var childTrunk = ChildTrunk(depth, trunkMask, isLast);
            chain.Add(canonical);
            Emit(rows, full, depth + 1, ignored, childTrunk, chain, cancellation);
            chain.Remove(canonical);
        }
    }

    private static long ChildTrunk(int depth, long trunkMask, bool isLast) =>
        depth == 0
            ? 0L
            : TreeGuides.SetKind(trunkMask, depth, isLast ? TreeGuide.None : TreeGuide.Through);

    // A declaration's own children are always shown with it: the unit of expansion is the file, so
    // there is no second chevron to place and the guides only ever branch off the file's trunk.
    private void EmitSymbols(
        List<FileBrowserRow> rows,
        string file,
        int depth,
        bool ignored,
        bool hidden,
        long trunkMask,
        CancellationToken cancellation)
    {
        var outline = Outline(file, cancellation);
        if (outline is null) return;
        Append(rows, file, outline.Roots, null, depth, ignored, hidden, trunkMask, cancellation);
    }

    private void Append(
        List<FileBrowserRow> rows,
        string file,
        IReadOnlyList<OutlineNode> nodes,
        string? parentPath,
        int depth,
        bool ignored,
        bool hidden,
        long trunkMask,
        CancellationToken cancellation)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();

            var node = nodes[i];
            var isLast = i == nodes.Count - 1;
            var guides = new TreeGuides(
                TreeGuides.SetKind(trunkMask, depth, isLast ? TreeGuide.Corner : TreeGuide.Tee),
                depth + 1);

            var symbolPath = FileOutline.PathOf(parentPath, node);
            var expandable = node.Children.Count > 0 && depth + 1 < _maxDepth;
            var open = expandable && !_closedSymbols.Contains(SymbolKey(file, symbolPath));

            rows.Add(new FileBrowserRow.Symbol(
                file, node.Name, depth, ignored, hidden, guides,
                node.Kind, node.ParameterTypes, node.StartLine, symbolPath, expandable, open));

            if (!open) continue;
            Append(
                rows, file, node.Children, symbolPath, depth + 1, ignored, hidden,
                ChildTrunk(depth, trunkMask, isLast), cancellation);
        }
    }

    private static string SymbolKey(string file, string symbolPath) => file + '\n' + symbolPath;

    private FileOutline? Outline(string file, CancellationToken cancellation)
    {
        if (_outlines.TryGetValue(file, out var hit)) return hit;

        var outline = _outline?.Invoke(file, cancellation);
        _outlines[file] = outline;
        return outline;
    }

    private bool CanDescend(
        string full, FileSystemEntry entry, int depth, HashSet<string> chain, out string canonical)
    {
        canonical = full;
        if (depth + 1 >= _maxDepth) return false;
        if (entry.IsLink)
        {
            var target = _files.ResolveLinkTarget(full);
            if (target is null) return false;
            canonical = PathKey.Normalize(target);
        }
        return !chain.Contains(canonical);
    }

    private void ReconcileExpanded(string directory, IReadOnlyList<FileSystemEntry> entries)
    {
        if (_expanded.Count == 0) return;

        // Files count as present too: a file can be open, and dropping its entry here would close
        // every expanded file on the next flatten.
        var present = new HashSet<string>(PathKey.Comparer);
        foreach (var entry in entries)
            present.Add(Path.Combine(directory, entry.Name));

        foreach (var path in _expanded.ToArray())
        {
            if (!PathKey.Comparer.Equals(Path.GetDirectoryName(path), directory)) continue;
            if (!present.Contains(path)) _expanded.Remove(path);
        }
    }

    private Cached Listing(string directory, bool parentIgnored, CancellationToken cancellation)
    {
        if (_cache.TryGetValue(directory, out var hit)) return hit;

        var listing = _files.List(directory, cancellation);
        var ignored = NoIgnored;
        if (!parentIgnored && listing is DirectoryListing.Listed listed && listed.Entries.Count > 0)
        {
            var paths = new List<string>(listed.Entries.Count);
            foreach (var entry in listed.Entries)
                if (!IsGitDirectory(entry.Name))
                    paths.Add(Relative(directory, entry));
            if (paths.Count > 0) ignored = _ignore.Ignored(paths);
        }

        var cached = new Cached(listing, ignored);
        _cache[directory] = cached;
        return cached;
    }

    private string Relative(string directory, FileSystemEntry entry)
    {
        var relative = Path.GetRelativePath(RootPath, Path.Combine(directory, entry.Name)).Replace('\\', '/');
        return entry.IsDirectory ? relative + '/' : relative;
    }

    private string Canonical(string path) =>
        _files.ResolveLinkTarget(path) is { } target ? PathKey.Normalize(target) : path;

    private static bool IsGitDirectory(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static List<FileSystemEntry> Ordered(IReadOnlyList<FileSystemEntry> entries)
    {
        var ordered = new List<FileSystemEntry>(entries.Count);
        foreach (var entry in entries)
            if (!IsGitDirectory(entry.Name))
                ordered.Add(entry);

        ordered.Sort(Compare);
        return ordered;
    }

    private static int Compare(FileSystemEntry a, FileSystemEntry b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
    }

    private sealed record Cached(DirectoryListing Listing, IReadOnlySet<string> Ignored);
}
