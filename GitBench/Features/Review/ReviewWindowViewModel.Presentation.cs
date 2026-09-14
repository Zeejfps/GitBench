using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using ZGF.Observable;

namespace GitBench.Features.Review;

// The narrator's side of the window: focus and spotlight requests become lookups the stacked diff
// list services, and what the list reports back (selection, visible lines) is held here for the
// tools to read. Every member is UI-thread only; the lookups themselves complete from wherever
// their bound finishes first (the list, a timeout, a cancellation).
internal sealed partial class ReviewWindowViewModel : IReviewPresentation, IReviewPresentationSurface
{
    // How long a focus or spotlight waits for the file's diff before giving up. Long enough for a
    // large file's first load, short enough that a stuck load does not stall a walkthrough.
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    private readonly State<IReadOnlyList<ReviewSpotlight>> _spotlights = new(Array.Empty<ReviewSpotlight>());
    private readonly State<bool> _spotlightDim = new(false);
    private readonly State<DiffSelectionQuote?> _selectionQuote = new(null);
    private readonly State<ReviewVisibleLines?> _visible = new(null);

    // One focus in flight at a time: a newer one supersedes the older. Spotlight lookups are a
    // batch, superseded together by the next set.
    private ReviewLineLookup? _pendingFocus;
    private readonly List<ReviewLineLookup> _pendingSpotlights = new();

    // The list view servicing lookups, and the lookups issued while none was attached — the
    // window's body mounts after the range resolves, so an early request waits for it.
    private Action<ReviewLineLookup>? _service;
    private readonly List<ReviewLineLookup> _unserviced = new();

    public IReadable<IReadOnlyList<ReviewSpotlight>> Spotlights => _spotlights;
    public IReadable<bool> SpotlightDim => _spotlightDim;
    public IReadable<DiffSelectionQuote?> Selection => _selectionQuote;

    /// <summary>What the stacked list shows at the top of its viewport, for a narrator asking
    /// where the reviewer is. Null before the list has drawn.</summary>
    public IReadable<ReviewVisibleLines?> Visible => _visible;

    public Task<ReviewLineResolution> FocusLineAsync(ReviewLineRef line, CancellationToken ct) =>
        Focus(new ReviewLookupTarget.At(line), ct);

    public Task<ReviewLineResolution> FocusFileAsync(string path, CancellationToken ct) =>
        Focus(new ReviewLookupTarget.Header(path), ct);

    public async Task<IReadOnlyList<ReviewLineResolution>> SetSpotlightsAsync(
        IReadOnlyList<ReviewSpotlight> spotlights, bool dim, CancellationToken ct)
    {
        foreach (var old in _pendingSpotlights)
            old.TryComplete(new ReviewLineResolution.Unavailable(old.Path, "superseded by a newer spotlight set"));
        _pendingSpotlights.Clear();

        _spotlights.Value = spotlights.ToArray();
        _spotlightDim.Value = dim;

        var lookups = new List<ReviewLineLookup>(spotlights.Count);
        foreach (var spotlight in spotlights)
            lookups.Add(Start(new ReviewLookupTarget.Range(spotlight), ct));
        _pendingSpotlights.AddRange(lookups);

        return await Task.WhenAll(lookups.Select(l => l.Result)).ConfigureAwait(false);
    }

    public void ClearSpotlights()
    {
        _spotlights.Value = Array.Empty<ReviewSpotlight>();
        _spotlightDim.Value = false;
    }

    /// <summary>The range's files the reviewer has marked Viewed, for a narrator's state read.</summary>
    public IReadOnlyList<string> ViewedPaths()
    {
        var viewed = new List<string>();
        foreach (var file in Files())
            if (_reviewedFiles.IsViewed(file.Path)) viewed.Add(file.Path);
        return viewed;
    }

    IDisposable IReviewPresentationSurface.ServiceLookups(Action<ReviewLineLookup> service)
    {
        _service = service;
        var backlog = _unserviced.ToArray();
        _unserviced.Clear();
        foreach (var lookup in backlog)
            if (!lookup.IsCompleted) service(lookup);
        return new ServiceToken(this, service);
    }

    void IReviewPresentationSurface.ReportSelection(DiffSelectionQuote? quote) => _selectionQuote.Value = quote;

    void IReviewPresentationSurface.ReportVisible(ReviewVisibleLines? visible) => _visible.Value = visible;

    private Task<ReviewLineResolution> Focus(ReviewLookupTarget target, CancellationToken ct)
    {
        _pendingFocus?.TryComplete(
            new ReviewLineResolution.Unavailable(_pendingFocus.Path, "superseded by a later focus request"));
        var lookup = Start(target, ct);
        _pendingFocus = lookup;
        return lookup.Result;
    }

    // Issues a lookup bounded by the caller's token and the load timeout. The bound completes the
    // lookup from its own thread; the list finds it completed and drops it on its next pass.
    private ReviewLineLookup Start(ReviewLookupTarget target, CancellationToken ct)
    {
        var lookup = new ReviewLineLookup(target);
        var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(LookupTimeout);
        var registration = bound.Token.Register(() =>
        {
            if (ct.IsCancellationRequested)
                lookup.TryCancel();
            else
                lookup.TryComplete(new ReviewLineResolution.Unavailable(
                    target.Path, $"the diff did not load within {LookupTimeout.TotalSeconds:0} s"));
        });
        lookup.Result.ContinueWith(
            _ =>
            {
                registration.Dispose();
                bound.Dispose();
            },
            TaskScheduler.Default);

        if (_service is { } service)
            service(lookup);
        else
            _unserviced.Add(lookup);
        return lookup;
    }

    // Closing the window ends every wait: a narrator blocked on a focus learns the surface is gone
    // rather than waiting out the timeout.
    private void DisposePresentation()
    {
        _pendingFocus?.TryComplete(new ReviewLineResolution.Unavailable(_pendingFocus.Path, "the review window closed"));
        foreach (var lookup in _pendingSpotlights)
            lookup.TryComplete(new ReviewLineResolution.Unavailable(lookup.Path, "the review window closed"));
        foreach (var lookup in _unserviced)
            lookup.TryComplete(new ReviewLineResolution.Unavailable(lookup.Path, "the review window closed"));
        _pendingSpotlights.Clear();
        _unserviced.Clear();
        _spotlights.Dispose();
        _spotlightDim.Dispose();
        _selectionQuote.Dispose();
        _visible.Dispose();
    }

    private sealed class ServiceToken : IDisposable
    {
        private readonly ReviewWindowViewModel _owner;
        private readonly Action<ReviewLineLookup> _service;

        public ServiceToken(ReviewWindowViewModel owner, Action<ReviewLineLookup> service)
        {
            _owner = owner;
            _service = service;
        }

        public void Dispose()
        {
            if (ReferenceEquals(_owner._service, _service)) _owner._service = null;
        }
    }
}
