namespace GitBench.Features.Review;

/// <summary>
/// Resolves a <see cref="ReviewSession"/> into a concrete <see cref="ReviewStack"/> — the data seam
/// that decouples the review window's GUI from the git layer. Phase 3 binds the
/// <see cref="StubReviewStackSource"/> (a naive HEAD-relative walk on real commits) so the window can
/// be built and tuned before the range backend exists; Phase 4 swaps in the real <c>base..head</c>
/// implementation through the same single DI binding.
/// </summary>
internal interface IReviewStackSource
{
    Task<ReviewStack> LoadAsync(ReviewSession session, int cap);
}
