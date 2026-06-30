using GitBench.App;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

// Owns the set of open review windows as observable state. Subscribes to OpenReviewWindowMessage
// and, for each request, pins a per-window ReviewWindowViewModel to the requested repo+range so the
// window stays locked to that review regardless of the main window's active repo. ReviewWindowsView
// reflects this list into real OS windows. Mirrors DiffWindowsViewModel: it injects the services the
// per-window VM (and its own commit-details VM) need, then constructs them per request.
internal sealed class ReviewWindowsViewModel : IDisposable
{
    private readonly IMessageBus _bus;
    private readonly IReviewStackSource _source;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private readonly IDisposable _subscription;

    public ObservableList<ReviewWindowViewModel> Windows { get; } = new();

    public ReviewWindowsViewModel(
        IMessageBus bus,
        IReviewStackSource source,
        IRepoRegistry registry,
        IGitService gitService,
        IUiDispatcher dispatcher,
        ILocalizationService loc,
        PreferencesService preferences)
    {
        _bus = bus;
        _source = source;
        _registry = registry;
        _gitService = gitService;
        _dispatcher = dispatcher;
        _loc = loc;
        _preferences = preferences;
        _subscription = _bus.SubscribeScoped<OpenReviewWindowMessage>(OnOpenRequested);
    }

    private void OnOpenRequested(OpenReviewWindowMessage m)
    {
        var session = new ReviewSession(m.RepoId, m.HeadRef, m.HeadLabel, m.BaseRef, m.BaseLabel);
        // The window's own commit-details VM, opted out of the selection bus so the History pane's
        // selection never drives this window's right pane.
        var details = new CommitDetailsViewModel(
            _gitService, _registry, _dispatcher, _bus, _loc, _preferences, subscribeToSelection: false);
        Windows.Add(new ReviewWindowViewModel(session, _source, _dispatcher, details));
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
