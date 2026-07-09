namespace GitBench.Features.Review;

/// <summary>
/// The app-session store of review progress: which files a reviewer has marked Viewed, per branch,
/// surviving a review window's close and reopen for as long as the app runs. A mark records the
/// file's content identity at the moment it was viewed, so a file whose content later changes reads
/// as unviewed again (it needs re-reviewing) while its unchanged neighbours stay viewed. Keyed by
/// <c>(repo, head ref)</c> — the branch under review, independent of the base it's compared against.
/// Also remembers the reviewer's last explicit base pick per branch, so reopening a review defaults
/// to the base it was last reviewed against instead of re-guessing.
/// </summary>
internal interface IReviewProgressStore
{
    /// <summary>Whether <paramref name="path"/> is marked Viewed at exactly <paramref name="contentId"/>
    /// — a mark made against a different content identity (the file has since changed) reads false.</summary>
    bool IsViewed(Guid repoId, string headRef, string path, string? contentId);

    /// <summary>Records or clears a file's Viewed mark. Marking stores <paramref name="contentId"/> as
    /// the reviewed content; unmarking forgets it.</summary>
    void SetViewed(Guid repoId, string headRef, string path, string? contentId, bool viewed);

    /// <summary>The base ref the reviewer last explicitly picked for this branch, or null when they
    /// never picked one (or last picked Auto).</summary>
    string? PreferredBase(Guid repoId, string headRef);

    /// <summary>Remembers an explicit base pick for this branch; null (Auto) forgets it.</summary>
    void SetPreferredBase(Guid repoId, string headRef, string? baseRef);
}

internal sealed class ReviewProgressStore : IReviewProgressStore
{
    // (repo, head ref) → (path → the content identity the file was Viewed at).
    private readonly Dictionary<(Guid RepoId, string HeadRef), Dictionary<string, string?>> _byBranch = new();

    // (repo, head ref) → the base ref the reviewer last explicitly picked for that branch.
    private readonly Dictionary<(Guid RepoId, string HeadRef), string> _preferredBases = new();

    public bool IsViewed(Guid repoId, string headRef, string path, string? contentId) =>
        _byBranch.TryGetValue((repoId, headRef), out var marks)
        && marks.TryGetValue(path, out var viewedAt)
        && viewedAt == contentId;

    public void SetViewed(Guid repoId, string headRef, string path, string? contentId, bool viewed)
    {
        var key = (repoId, headRef);
        if (viewed)
        {
            if (!_byBranch.TryGetValue(key, out var marks))
                _byBranch[key] = marks = new Dictionary<string, string?>(StringComparer.Ordinal);
            marks[path] = contentId;
        }
        else if (_byBranch.TryGetValue(key, out var marks))
        {
            marks.Remove(path);
        }
    }

    public string? PreferredBase(Guid repoId, string headRef) =>
        _preferredBases.TryGetValue((repoId, headRef), out var baseRef) ? baseRef : null;

    public void SetPreferredBase(Guid repoId, string headRef, string? baseRef)
    {
        if (baseRef == null)
            _preferredBases.Remove((repoId, headRef));
        else
            _preferredBases[(repoId, headRef)] = baseRef;
    }
}
