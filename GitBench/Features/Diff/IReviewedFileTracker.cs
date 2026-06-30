using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// Tracks which <c>(commit, file)</c> pairs the reviewer has marked "Viewed". Resolved optionally from
/// the build context (<c>ctx.Get</c>): a review window provides one into its subtree, so the shared
/// diff-pane header shows a Viewed toggle there; the History pane and Local Changes provide none, so
/// the toggle is absent for them. <see cref="Revision"/> bumps on every change so widgets bound through
/// an auto-tracked compute refresh.
/// </summary>
internal interface IReviewedFileTracker
{
    IReadable<int> Revision { get; }

    bool IsViewed(string sha, string path);

    void ToggleViewed(string sha, string path);

    /// <summary>The set of viewed file paths for one commit, for deriving per-increment progress.</summary>
    IReadOnlySet<string> ViewedPaths(string sha);
}
