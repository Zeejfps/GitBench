namespace GitBench.Features.Review;

/// <summary>
/// Resolves a <see cref="ReviewSession"/> into a concrete <see cref="ReviewStack"/> — the data seam
/// that decouples the review window's GUI from the git layer.
/// </summary>
internal interface IReviewStackSource
{
    Task<ReviewStack> LoadAsync(ReviewSession session, int cap);
}
