using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

// Owns the set of open review windows as observable state. Subscribes to OpenReviewWindowMessage
// and, for each request, pins a per-window ReviewWindowViewModel to the requested repo+range so
// the window stays locked to that review regardless of the main window's active repo.
// ReviewWindowsView reflects this list into real OS windows. Mirrors DiffWindowsViewModel.
//
// Phase 2 needs only the message bus; later phases add the stack-source/git dependencies the
// per-window VM requires to actually load the range.
internal sealed class ReviewWindowsViewModel : IDisposable
{
    private readonly IMessageBus _bus;
    private readonly IDisposable _subscription;

    public ObservableList<ReviewWindowViewModel> Windows { get; } = new();

    public ReviewWindowsViewModel(IMessageBus bus)
    {
        _bus = bus;
        _subscription = _bus.SubscribeScoped<OpenReviewWindowMessage>(OnOpenRequested);
    }

    private void OnOpenRequested(OpenReviewWindowMessage m)
    {
        var session = new ReviewSession(m.RepoId, m.HeadRef, m.HeadLabel, m.BaseRef, m.BaseLabel);
        Windows.Add(new ReviewWindowViewModel(session));
    }

    // Removes a window (e.g. the user clicked the native close button). The removal drives the
    // view to tear down the OS window; we then dispose the window's view model.
    public void Close(ReviewWindowViewModel window)
    {
        if (Windows.Remove(window))
            window.Dispose();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        foreach (var w in Windows) w.Dispose();
        Windows.Clear();
    }
}
