using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// A review window's view of which of its files the reviewer has marked "Viewed", scoped to the one
/// branch that window reviews. Resolved optionally from the build context (<c>ctx.Get</c>): a review
/// window provides one into its subtree, so the shared diff-pane header shows a Viewed toggle there;
/// the History pane and Local Changes provide none, so the toggle is absent for them. Each file is
/// addressed by its path; the tracker knows the file's current content identity, so a mark made
/// before the file changed no longer reads as Viewed. <see cref="Revision"/> bumps on every change
/// (a toggle, or a range reload that shifts a file's content) so widgets bound through an auto-tracked
/// compute refresh.
/// </summary>
internal interface IReviewedFileTracker
{
    IReadable<int> Revision { get; }

    bool IsViewed(string path);

    void ToggleViewed(string path);

    /// <summary>Marks every given path viewed / not viewed, bumping <see cref="Revision"/> once.</summary>
    void SetViewed(IReadOnlyList<string> paths, bool viewed);

    /// <summary>
    /// Paths whose mark is being written and not yet reflected by <see cref="IsViewed"/> — a tracker
    /// whose marks live in git (the working-tree review's staged state) has a round-trip between the
    /// request and the answer, and the file list draws those rows as in flight meanwhile. A tracker
    /// that marks synchronously never has any.
    /// </summary>
    IReadable<IReadOnlySet<string>> InFlight => NoneInFlight;

    private static readonly IReadable<IReadOnlySet<string>> NoneInFlight =
        new State<IReadOnlySet<string>>(new HashSet<string>());
}
