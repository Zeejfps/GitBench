using GitBench.Features.Commits;
using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The working-tree review's <see cref="IReviewedFileTracker"/>: a file's "mark" is its staged state,
/// and checking the box stages it. There is no separate reviewed-state store here — the git index is
/// the single source of truth, so a mark survives a restart, and git's own rules decide what a
/// partially-staged file looks like.
///
/// A path counts as marked only when it is <em>fully</em> staged: present in the staged list and
/// absent from the unstaged one. A file staged and then edited again shows up on both sides, so it
/// correctly reads as unmarked — there is still working-tree content that no commit would capture.
/// </summary>
internal sealed class StagedFileTracker : IReviewedFileTracker, IDisposable
{
    private readonly LocalChangesViewModel _local;
    private readonly State<int> _revision = new(0);
    private readonly List<IDisposable> _subscriptions = new();

    private HashSet<string> _fullyStaged = new(StringComparer.Ordinal);
    private HashSet<string> _partlyStaged = new(StringComparer.Ordinal);

    public StagedFileTracker(LocalChangesViewModel local)
    {
        _local = local;
        // The atomic pair, not the two side slices — a handler fired by one slice can read the
        // other pre-update, which would transiently mark a mid-stage file as neither staged nor partial.
        _subscriptions.Add(_local.WorkingTreeLists.Subscribe(Recompute));
    }

    public IReadable<int> Revision => _revision;

    public bool IsViewed(string path) => _fullyStaged.Contains(path);

    /// <summary>Whether the file has staged content <em>and</em> further unstaged edits on top — the
    /// checkbox's indeterminate state. Staging it captures the rest; unstaging it drops what's staged.</summary>
    public bool IsPartiallyStaged(string path) => _partlyStaged.Contains(path);

    /// <summary>Whether any of the file's content is staged, fully or partly. The paths "Unstage" can
    /// meaningfully act on.</summary>
    public bool HasStagedContent(string path) => _fullyStaged.Contains(path) || _partlyStaged.Contains(path);

    public void ToggleViewed(string path) => SetViewed([path], !IsViewed(path));

    /// <summary>
    /// Stages / unstages the given paths, skipping the ones already there so a group action on a mixed
    /// selection only touches what must move. Staging targets anything not <em>fully</em> staged (a
    /// partially staged file has more to capture); unstaging targets anything with <em>any</em> staged
    /// content (so a partially staged file can be emptied back out).
    /// </summary>
    public void SetViewed(IReadOnlyList<string> paths, bool viewed)
    {
        if (paths.Count == 0) return;
        var targets = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (viewed ? !IsViewed(p) : HasStagedContent(p)) targets.Add(p);
        }
        if (targets.Count == 0) return;

        // The index op moves the file between the staged / unstaged lists, which bumps Revision
        // through Recompute — optimistically, before git returns.
        if (viewed) _local.Stage(targets);
        else _local.Unstage(targets);
    }

    private void Recompute((IReadOnlyList<FileChange> Unstaged, IReadOnlyList<FileChange> Staged) lists)
    {
        var unstaged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in lists.Unstaged) unstaged.Add(f.Path);

        var full = new HashSet<string>(StringComparer.Ordinal);
        var partial = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in lists.Staged)
            (unstaged.Contains(f.Path) ? partial : full).Add(f.Path);

        if (full.SetEquals(_fullyStaged) && partial.SetEquals(_partlyStaged)) return;
        _fullyStaged = full;
        _partlyStaged = partial;
        _revision.Value++;
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _revision.Dispose();
    }
}
