namespace GitBench.Features.Review;

/// <summary>
/// The app-session store of review progress: which files a reviewer has marked Viewed, per branch,
/// surviving a review window's close and reopen for as long as the app runs. A mark records the
/// file's content identity at the moment it was viewed, so a file whose content later changes reads
/// as unviewed again (it needs re-reviewing) while its unchanged neighbours stay viewed. Keyed by
/// <c>(repo, head ref)</c> — the branch under review, independent of the base it's compared against.
/// </summary>
internal interface IReviewProgressStore
{
    /// <summary>Whether <paramref name="path"/> is marked Viewed at exactly <paramref name="contentId"/>
    /// — a mark made against a different content identity (the file has since changed) reads false.</summary>
    bool IsViewed(Guid repoId, string headRef, string path, string? contentId);

    /// <summary>Records or clears a file's Viewed mark. Marking stores <paramref name="contentId"/> as
    /// the reviewed content; unmarking forgets it.</summary>
    void SetViewed(Guid repoId, string headRef, string path, string? contentId, bool viewed);
}

internal sealed class ReviewProgressStore : IReviewProgressStore
{
    // (repo, head ref) → (path → the content identity the file was Viewed at).
    private readonly Dictionary<(Guid RepoId, string HeadRef), Dictionary<string, string?>> _byBranch = new();

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
}
