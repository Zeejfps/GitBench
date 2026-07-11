using GitBench.App;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// Owns the set of open cross-repo review windows as observable state — the change-set analogue of
/// <see cref="ReviewWindowsViewModel"/>. Subscribes to <see cref="OpenChangeSetReviewMessage"/> and,
/// per request, pins a <see cref="ChangeSetReviewViewModel"/> to the requested session (its own
/// commit-details VM opted out of the selection bus). Dedupes on the sorted member <c>(RepoId, HeadRef)</c>
/// pairs so re-opening the same set focuses the existing window instead of stacking duplicates.
/// <c>ChangeSetReviewWindowsView</c> reflects this list into real OS windows.
/// </summary>
internal sealed class ChangeSetReviewWindowsViewModel : IDisposable
{
    private readonly IMessageBus _bus;
    private readonly IReviewStackSource _source;
    private readonly IRepoRegistry _registry;
    private readonly IGitService _gitService;
    private readonly IReviewProgressStore _reviewProgress;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _loc;
    private readonly PreferencesService _preferences;
    private readonly IRepoStatusStore _status;
    private readonly IDisposable _subscription;

    public ObservableList<ChangeSetReviewViewModel> Windows { get; } = new();

    // Raised when an open request matches an already-open window (same set of members). The view
    // focuses that window's OS window instead of opening a duplicate.
    public event Action<ChangeSetReviewViewModel>? FocusRequested;

    public ChangeSetReviewWindowsViewModel(
        IMessageBus bus,
        IReviewStackSource source,
        IRepoRegistry registry,
        IGitService gitService,
        IReviewProgressStore reviewProgress,
        IUiDispatcher dispatcher,
        ILocalizationService loc,
        PreferencesService preferences,
        IRepoStatusStore status)
    {
        _bus = bus;
        _source = source;
        _registry = registry;
        _gitService = gitService;
        _reviewProgress = reviewProgress;
        _dispatcher = dispatcher;
        _loc = loc;
        _preferences = preferences;
        _status = status;
        _subscription = _bus.SubscribeScoped<OpenChangeSetReviewMessage>(OnOpenRequested);
    }

    private void OnOpenRequested(OpenChangeSetReviewMessage m)
    {
        var key = DedupeKey(m.Session);
        var existing = FindOpenWindow(key);
        if (existing != null)
        {
            FocusRequested?.Invoke(existing);
            return;
        }

        var details = new CommitDetailsViewModel(
            _gitService, _registry, _dispatcher, _bus, _loc, _preferences, subscribeToSelection: false);
        Windows.Add(new ChangeSetReviewViewModel(
            m.Session, _source, _dispatcher, details, _loc, _bus, _reviewProgress, _registry, _status));
    }

    private ChangeSetReviewViewModel? FindOpenWindow(string key)
    {
        foreach (var w in Windows)
            if (DedupeKey(w.Session) == key)
                return w;
        return null;
    }

    // Sorted member (RepoId, HeadRef) pairs: membership + heads identify the set, order-independent, so
    // re-opening the same set from any member's menu focuses the one window.
    private static string DedupeKey(ChangeSetSession session)
    {
        var parts = session.Members
            .Select(x => $"{x.RepoId:N}@{x.HeadRef}")
            .OrderBy(x => x, StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    public void Close(ChangeSetReviewViewModel window)
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
