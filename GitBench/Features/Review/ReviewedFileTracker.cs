using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// A review window's per-window store of marked-Viewed files, keyed by commit sha then path. Ephemeral
/// for the window's lifetime (Phase 5b); persistence and content-hash auto-uncheck on amend ride a
/// later phase. Provided into the window's subtree as the <see cref="IReviewedFileTracker"/> the shared
/// diff-pane header consults.
/// </summary>
internal sealed class ReviewedFileTracker : IReviewedFileTracker, IDisposable
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    private readonly Dictionary<string, HashSet<string>> _byCommit = new();
    private readonly State<int> _revision = new(0);

    public IReadable<int> Revision => _revision;

    public bool IsViewed(string sha, string path) =>
        _byCommit.TryGetValue(sha, out var set) && set.Contains(path);

    public void ToggleViewed(string sha, string path)
    {
        if (!_byCommit.TryGetValue(sha, out var set))
            _byCommit[sha] = set = new HashSet<string>();
        if (!set.Remove(path)) set.Add(path);
        _revision.Value++;
    }

    public IReadOnlySet<string> ViewedPaths(string sha) =>
        _byCommit.TryGetValue(sha, out var set) ? set : Empty;

    public void Dispose() => _revision.Dispose();
}
