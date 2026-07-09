using GitBench.Features.Commits;

namespace GitBench.Features.Review;

/// <summary>
/// The review window's selected files, owned by <see cref="ReviewWindowViewModel"/> and rendered as
/// the file tree's highlighted rows. <see cref="Paths"/> is kept in range order, so <see cref="Lead"/>
/// — the first selected file — is the one the diff surface focuses on.
///
/// <see cref="Anchor"/> is the pivot a Shift-click / Shift-arrow range extends from; <see cref="Cursor"/>
/// is the row the last gesture landed on, so arrow keys keep stepping from where the user left off.
/// <see cref="Create"/> is the only constructor: it drops paths (and an anchor / cursor) that the
/// loaded range no longer contains, so a reload can't strand the selection on a file that went away.
/// </summary>
internal sealed class ReviewSelection
{
    public static readonly ReviewSelection Empty = new(Array.Empty<string>(), null, null);

    /// <summary>The selected file paths, in range order.</summary>
    public IReadOnlyList<string> Paths { get; }

    public string? Anchor { get; }
    public string? Cursor { get; }

    private readonly HashSet<string> _set;

    private ReviewSelection(IReadOnlyList<string> paths, string? anchor, string? cursor)
    {
        Paths = paths;
        Anchor = anchor;
        Cursor = cursor;
        _set = new HashSet<string>(paths, StringComparer.Ordinal);
    }

    public int Count => Paths.Count;

    /// <summary>The focused file: the first of the selection in range order, or null when empty.</summary>
    public string? Lead => Paths.Count > 0 ? Paths[0] : null;

    public IReadOnlySet<string> Set => _set;

    public bool Contains(string path) => _set.Contains(path);

    public static ReviewSelection Create(
        IReadOnlyList<string> paths,
        string? anchor,
        string? cursor,
        IReadOnlyList<FileChange> files)
    {
        if (paths.Count == 0 && anchor == null && cursor == null) return Empty;

        var order = new Dictionary<string, int>(files.Count, StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++) order[files[i].Path] = i;

        var kept = new List<string>(paths.Count);
        foreach (var p in paths)
            if (order.ContainsKey(p) && !kept.Contains(p)) kept.Add(p);
        kept.Sort((a, b) => order[a].CompareTo(order[b]));

        var keptAnchor = anchor != null && order.ContainsKey(anchor) ? anchor : null;
        var keptCursor = cursor != null && order.ContainsKey(cursor) ? cursor : null;

        return kept.Count == 0 && keptAnchor == null && keptCursor == null
            ? Empty
            : new ReviewSelection(kept, keptAnchor, keptCursor);
    }

    public static ReviewSelection Single(string path, IReadOnlyList<FileChange> files) =>
        Create([path], path, path, files);
}
