using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The cross-repo review window's <see cref="IReviewedFileTracker"/>: aggregates N members' Viewed
/// state behind one tracker keyed by repo-qualified path. A mark on <c>"&lt;repoKey&gt;/&lt;path&gt;"</c>
/// resolves back to the owning member and routes to its own <c>(RepoId, HeadRef)</c> progress key in the
/// shared <see cref="IReviewProgressStore"/> — so marks compose with single-repo reviews of the same
/// branch for free (Locked decision #2). Per-file content identity comes from the loaded ranges'
/// after-side blobs (<see cref="SetFingerprints"/>), so a file changed since it was viewed re-opens.
/// </summary>
internal sealed class ChangeSetReviewedFiles : IReviewedFileTracker, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string?> NoFingerprints =
        new Dictionary<string, string?>();

    private readonly IReviewProgressStore _store;
    // repoKey → the member that owns files under that top-level folder.
    private readonly IReadOnlyDictionary<string, (Guid RepoId, string HeadRef)> _memberByKey;
    private readonly State<int> _revision = new(0);

    private IReadOnlyDictionary<string, string?> _fingerprints = NoFingerprints;

    public ChangeSetReviewedFiles(
        IReviewProgressStore store, IReadOnlyDictionary<string, (Guid RepoId, string HeadRef)> memberByKey)
    {
        _store = store;
        _memberByKey = memberByKey;
    }

    public IReadable<int> Revision => _revision;

    public bool IsViewed(string path)
        => Resolve(path, out var repoId, out var headRef, out var unqualified)
           && _store.IsViewed(repoId, headRef, unqualified, Fingerprint(path));

    public void ToggleViewed(string path)
    {
        if (!Resolve(path, out var repoId, out var headRef, out var unqualified)) return;
        var contentId = Fingerprint(path);
        var viewed = _store.IsViewed(repoId, headRef, unqualified, contentId);
        _store.SetViewed(repoId, headRef, unqualified, contentId, !viewed);
        _revision.Value++;
    }

    public void SetViewed(IReadOnlyList<string> paths, bool viewed)
    {
        if (paths.Count == 0) return;
        foreach (var path in paths)
            if (Resolve(path, out var repoId, out var headRef, out var unqualified))
                _store.SetViewed(repoId, headRef, unqualified, Fingerprint(path), viewed);
        _revision.Value++;
    }

    /// <summary>Adopts the loaded ranges' per-(qualified)path content identities; the bump refreshes
    /// every bound Viewed mark, and a file whose identity shifted flips back to unviewed.</summary>
    public void SetFingerprints(IReadOnlyDictionary<string, string?> fingerprints)
    {
        _fingerprints = fingerprints;
        _revision.Value++;
    }

    private bool Resolve(string qualified, out Guid repoId, out string headRef, out string path)
    {
        if (RepoQualifiedPaths.TryResolve(qualified, _memberByKey, out var member, out path))
        {
            repoId = member.RepoId;
            headRef = member.HeadRef;
            return true;
        }
        repoId = default;
        headRef = string.Empty;
        return false;
    }

    private string? Fingerprint(string path) =>
        _fingerprints.TryGetValue(path, out var contentId) ? contentId : null;

    public void Dispose() => _revision.Dispose();
}
