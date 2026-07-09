using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// One review window's <see cref="IReviewedFileTracker"/>: a branch-scoped view over the app-session
/// <see cref="IReviewProgressStore"/>. Marks persist in the store keyed by the window's branch, so
/// they outlive the window; this facade adds the per-file content identity (the current range's
/// after-side blob per path) the store needs to tell whether a marked file has since changed. The
/// window refreshes that fingerprint map whenever the range's files load (<see cref="SetFingerprints"/>).
/// </summary>
internal sealed class BranchReviewedFiles : IReviewedFileTracker, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string?> NoFingerprints =
        new Dictionary<string, string?>();

    private readonly IReviewProgressStore _store;
    private readonly Guid _repoId;
    private readonly string _headRef;
    private readonly State<int> _revision = new(0);

    private IReadOnlyDictionary<string, string?> _fingerprints = NoFingerprints;

    public BranchReviewedFiles(IReviewProgressStore store, Guid repoId, string headRef)
    {
        _store = store;
        _repoId = repoId;
        _headRef = headRef;
    }

    public IReadable<int> Revision => _revision;

    public bool IsViewed(string path) => _store.IsViewed(_repoId, _headRef, path, Fingerprint(path));

    public void ToggleViewed(string path)
    {
        var contentId = Fingerprint(path);
        var viewed = _store.IsViewed(_repoId, _headRef, path, contentId);
        _store.SetViewed(_repoId, _headRef, path, contentId, !viewed);
        _revision.Value++;
    }

    public void SetViewed(IReadOnlyList<string> paths, bool viewed)
    {
        if (paths.Count == 0) return;
        foreach (var path in paths)
            _store.SetViewed(_repoId, _headRef, path, Fingerprint(path), viewed);
        _revision.Value++;
    }

    // Adopts the loaded range's per-file content identities. A file whose identity shifted (or dropped
    // out of the range) is re-evaluated against the store, so it flips back to unviewed when changed;
    // the revision bump refreshes every bound Viewed mark.
    public void SetFingerprints(IReadOnlyDictionary<string, string?> fingerprints)
    {
        _fingerprints = fingerprints;
        _revision.Value++;
    }

    private string? Fingerprint(string path) =>
        _fingerprints.TryGetValue(path, out var contentId) ? contentId : null;

    public void Dispose() => _revision.Dispose();
}
