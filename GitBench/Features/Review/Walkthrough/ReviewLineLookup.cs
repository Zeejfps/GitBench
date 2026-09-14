using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>Where on the diff surface a narrator wants to land, and whether landing there moves
/// the viewport.</summary>
internal abstract record ReviewLookupTarget(string Path)
{
    /// <summary>The file's header onto the pin line, unfolded. Resolves to the first line its diff
    /// holds, so a narrator learns where the change starts.</summary>
    public sealed record Header(string Path) : ReviewLookupTarget(Path);

    /// <summary>One line, scrolled about a third of the way down the viewport.</summary>
    public sealed record At(ReviewLineRef Line) : ReviewLookupTarget(Line.Path);

    /// <summary>A spotlight's range, resolved to its text without moving the viewport.</summary>
    public sealed record Range(ReviewSpotlight Spotlight) : ReviewLookupTarget(Spotlight.Path);
}

/// <summary>
/// One narrator request in flight between the review window model and the stacked diff list.
/// The list services it across frames — waiting for the file's diff to load, revealing a gap,
/// letting a scroll settle — and completes it; the model completes it instead when the request
/// is superseded, times out, or is cancelled. Completion is thread-safe and first-wins.
/// </summary>
internal sealed class ReviewLineLookup
{
    private readonly TaskCompletionSource<ReviewLineResolution> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReviewLineLookup(ReviewLookupTarget target) => Target = target;

    public ReviewLookupTarget Target { get; }

    public string Path => Target.Path;

    public Task<ReviewLineResolution> Result => _completion.Task;

    public bool IsCompleted => _completion.Task.IsCompleted;

    public bool TryComplete(ReviewLineResolution resolution) => _completion.TrySetResult(resolution);

    public bool TryCancel() => _completion.TrySetCanceled();
}

/// <summary>The lines of one file the stacked diff list currently shows at the top of its
/// viewport, numbered as a reader cites them (after side, or before side for removed lines).</summary>
internal sealed record ReviewVisibleLines(string Path, FileLine From, FileLine To);

/// <summary>
/// The narrator-facing side of a review window as the stacked diff list sees it: the lookups to
/// service, the spotlights to draw, and where to report what the reviewer has selected and sees.
/// </summary>
internal interface IReviewPresentationSurface
{
    /// <summary>Hands every pending and future lookup to <paramref name="service"/> until the
    /// returned token is disposed. One servicer at a time; the list attaches on mount.</summary>
    IDisposable ServiceLookups(Action<ReviewLineLookup> service);

    IReadable<IReadOnlyList<ReviewSpotlight>> Spotlights { get; }

    IReadable<bool> SpotlightDim { get; }

    void ReportSelection(DiffSelectionQuote? quote);

    void ReportVisible(ReviewVisibleLines? visible);
}
