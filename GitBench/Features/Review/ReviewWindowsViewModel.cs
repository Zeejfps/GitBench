using GitBench.App;
using GitBench.Features.CodeIntel;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>The open review windows as a reader sees them — a list to read on the UI thread, in
/// the order they were opened. <see cref="ReviewWindowsViewModel"/> is the app's one registry; the
/// assistant's review tools point through this seam, and a test with no windows passes an empty
/// one.</summary>
internal interface IReviewWindowRegistry
{
    IReadOnlyList<ReviewWindowViewModel> Windows { get; }
}

internal static class ReviewWindowRegistryExtensions
{
    /// <summary>The window a repository's narrator means: several may be open for one repository
    /// (one per head), and the most recently opened is the one being read. UI thread only.</summary>
    public static ReviewWindowViewModel? LatestFor(this IReviewWindowRegistry registry, Guid repoId)
    {
        var windows = registry.Windows;
        for (var i = windows.Count - 1; i >= 0; i--)
            if (windows[i].Session.RepoId == repoId)
                return windows[i];
        return null;
    }
}

// Owns the set of open review windows as observable state. Subscribes to OpenReviewWindowMessage
// and, for each request, pins a per-window ReviewWindowViewModel to the requested repo+range so the
// window stays locked to that review regardless of the main window's active repo. ReviewWindowsView
// reflects this list into real OS windows. Mirrors DiffWindowsViewModel: it injects the services the
// per-window VM (and its own commit-details VM) need, then constructs them per request.
internal sealed class ReviewWindowsViewModel : IReviewWindowRegistry, IDisposable
{
    private readonly IMessageBus _bus;
    private readonly IReviewStackSource _source;
    private readonly IRepoRegistry _registry;
    private readonly IGitHistoryReader _gitHistory;
    private readonly IGitDiffReader _gitDiff;
    private readonly IGitWorkingTreeOperations _gitWorkingTree;
    private readonly IGitConflictOperations _gitConflicts;
    private readonly IGitSubmoduleOperations _gitSubmodules;
    private readonly ISymbolExtractor _extractor;
    private readonly IRepoSnapshotStore _snapshots;
    private readonly IReviewProgressStore _reviewProgress;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private readonly IDisposable _subscription;
    private bool _disposed;

    public ObservableList<ReviewWindowViewModel> Windows { get; } = new();

    IReadOnlyList<ReviewWindowViewModel> IReviewWindowRegistry.Windows => Windows;

    // Raised when an open request matches an already-open window (same repo + head ref). The view
    // focuses that window's OS window instead of opening a duplicate — a review is a place you
    // return to. The DiffWindows template has no such dedupe; this is the review window's addition.
    public event Action<ReviewWindowViewModel>? FocusRequested;

    public ReviewWindowsViewModel(
        IMessageBus bus,
        IReviewStackSource source,
        IRepoRegistry registry,
        IGitHistoryReader gitHistory,
        IGitDiffReader gitDiff,
        IGitWorkingTreeOperations gitWorkingTree,
        IGitConflictOperations gitConflicts,
        IGitSubmoduleOperations gitSubmodules,
        ISymbolExtractor extractor,
        IRepoSnapshotStore snapshots,
        IReviewProgressStore reviewProgress,
        IUiDispatcher dispatcher,
        ILocalizationService loc,
        PreferencesService preferences)
    {
        _bus = bus;
        _source = source;
        _registry = registry;
        _gitHistory = gitHistory;
        _gitDiff = gitDiff;
        _gitWorkingTree = gitWorkingTree;
        _gitConflicts = gitConflicts;
        _gitSubmodules = gitSubmodules;
        _extractor = extractor;
        _snapshots = snapshots;
        _reviewProgress = reviewProgress;
        _dispatcher = dispatcher;
        _loc = loc;
        _preferences = preferences;
        _subscription = _bus.SubscribeScoped<OpenReviewWindowMessage>(OnOpenRequested);
    }

    private void OnOpenRequested(OpenReviewWindowMessage m)
    {
        // Singleton-per-(repo, head): focus the existing review instead of opening a duplicate.
        var existing = FindOpenWindow(m.RepoId, m.HeadRef);
        if (existing != null)
        {
            FocusRequested?.Invoke(existing);
            return;
        }

        var session = new ReviewSession(m.RepoId, m.HeadRef, m.HeadLabel, m.BaseRef, m.BaseLabel);
        // The window's own commit-details VM, opted out of the selection bus so the History pane's
        // selection never drives this window's right pane.
        var details = new CommitDetailsViewModel(
            _gitHistory, _gitDiff, _gitWorkingTree, _gitConflicts, _gitSubmodules, _extractor, _registry, _dispatcher, _bus, _loc, _preferences, subscribeToSelection: false);
        Windows.Add(new ReviewWindowViewModel(
            session, _source, _dispatcher, details, _loc, _bus, _snapshots, _reviewProgress));
    }

    private ReviewWindowViewModel? FindOpenWindow(Guid repoId, string headRef)
    {
        foreach (var w in Windows)
            if (w.Session.RepoId == repoId && w.Session.HeadRef == headRef)
                return w;
        return null;
    }

    // Removes a window (e.g. the user clicked the native close button). The removal drives the
    // view to tear down the OS window; we then dispose the window's view model.
    public void Close(ReviewWindowViewModel window)
    {
        if (Windows.Remove(window))
            window.Dispose();
    }

    // Idempotent: the hosting view disposes it on unmount and the app context disposes it as a
    // singleton, and either may come first at shutdown.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
        foreach (var w in Windows) w.Dispose();
        Windows.Clear();
    }
}
