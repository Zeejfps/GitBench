namespace GitBench.Features.Diff;

/// <summary>
/// Builds the identity keys under which <see cref="IReviewedFileTracker"/> stores per-file Viewed
/// marks: a commit's own sha for a commit diff, a short <c>base..head</c> composite for a combined
/// range diff. Every surface that reads or writes a mark (the Changes list, the tab strip, the diff
/// header, the review window's progress) derives its key here so they always agree.
/// </summary>
internal static class ReviewFileKey
{
    public static string ForRange(string baseSha, string headSha) => $"{Short(baseSha)}..{Short(headSha)}";

    private static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];
}
