namespace GitBench.Features.Review;

// One open review window, pinned to a single ReviewSession. Phase 2 is minimal: it holds the
// session and a derived title. Later phases promote it to a ViewModelBase<ReviewState> that loads
// the stack, tracks selection and reviewed-state, and drives the commit-details surface.
internal sealed class ReviewWindowViewModel : IDisposable
{
    public ReviewSession Session { get; }
    public string Title { get; }

    public ReviewWindowViewModel(ReviewSession session)
    {
        Session = session;
        Title = $"Review: {session.HeadLabel}";
    }

    public void Dispose() { }
}
