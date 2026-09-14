using GitBench.Features.Diff;
using GitBench.Features.Review.Walkthrough;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// A clock the test advances by hand. Timers fire, on the advancing thread, when their due time is
/// reached; what they post is then the test's to drain.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private long _nowTicks = DateTimeOffset.UnixEpoch.Ticks;

    public override DateTimeOffset GetUtcNow() => new(_nowTicks, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _nowTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public int LiveTimers
    {
        get
        {
            lock (_timers) return _timers.Count(t => t.DueTicks is not null);
        }
    }

    public void Advance(TimeSpan by)
    {
        var target = _nowTicks + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
                next = _timers.Where(t => t.DueTicks is { } due && due <= target).MinBy(t => t.DueTicks);
            if (next is null) break;
            _nowTicks = next.DueTicks!.Value;
            next.Fire();
        }

        _nowTicks = target;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_timers) _timers.Remove(timer);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long? DueTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._nowTicks + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            DueTicks = null;
            _callback(_state);
        }

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Stands in for the review window's diff surface: records every call, answers focus and
/// spotlights as the test scripts, and carries a selection the test sets.
/// </summary>
internal sealed class RecordingPresentation : IReviewPresentation
{
    private readonly State<IReadOnlyList<ReviewSpotlight>> _spotlights = new(Array.Empty<ReviewSpotlight>());
    private readonly State<bool> _dim = new(false);

    public List<string> Calls { get; } = new();

    public State<DiffSelectionQuote?> SelectionState { get; } = new(null);

    /// <summary>How a focus resolves; resolved by default.</summary>
    public Func<ReviewLineRef, ReviewLineResolution> FocusAnswer { get; set; } =
        line => new ReviewLineResolution.Resolved(line, "text");

    public Task<ReviewLineResolution> FocusLineAsync(ReviewLineRef line, CancellationToken ct)
    {
        Calls.Add($"focus {line.Path}:{line.Line.Value}:{line.Side}");
        return Task.FromResult(FocusAnswer(line));
    }

    public Task<ReviewLineResolution> FocusFileAsync(string path, CancellationToken ct)
    {
        Calls.Add($"focus-file {path}");
        return Task.FromResult<ReviewLineResolution>(new ReviewLineResolution.Resolved(
            new ReviewLineRef(path, DiffLineSide.New, new FileLine(1)), "text"));
    }

    public Task<IReadOnlyList<ReviewLineResolution>> SetSpotlightsAsync(
        IReadOnlyList<ReviewSpotlight> spotlights, bool dim, CancellationToken ct)
    {
        Calls.Add($"spotlights {spotlights.Count} dim={dim}");
        _spotlights.Value = spotlights;
        _dim.Value = dim;
        var resolutions = spotlights
            .Select(ReviewLineResolution (s) => new ReviewLineResolution.Resolved(new ReviewLineRef(s.Path, s.Side, s.From), "text"))
            .ToList();
        return Task.FromResult<IReadOnlyList<ReviewLineResolution>>(resolutions);
    }

    public void ClearSpotlights()
    {
        Calls.Add("clear");
        _spotlights.Value = Array.Empty<ReviewSpotlight>();
        _dim.Value = false;
    }

    public IReadable<IReadOnlyList<ReviewSpotlight>> Spotlights => _spotlights;
    public IReadable<bool> SpotlightDim => _dim;
    public IReadable<DiffSelectionQuote?> Selection => SelectionState;
}

internal static class WalkthroughSteps
{
    public static readonly Narrator Agent = new(NarratorKind.McpSession, "Terminal agent");

    public static readonly Narrator AssistantNarrator = new(NarratorKind.Assistant, "Assistant");

    public static WalkthroughStep Step(string title, string? focusPath = null, int line = 1, params ReviewSpotlight[] spotlights) =>
        new(
            title,
            $"Body of {title}",
            focusPath is null ? null : new ReviewLineRef(focusPath, DiffLineSide.New, new FileLine(line)),
            spotlights);

    public static ReviewSpotlight Spot(string path, int from, int to, string? note = null) =>
        new(path, DiffLineSide.New, new FileLine(from), new FileLine(to), note);
}

internal static class WalkthroughExchanges
{
    /// <summary>The store's current exchange as one line per message: <c>You: …</c>, the
    /// narration's text as it stands, <c>Failed: …</c> or <c>Refused: …</c>.</summary>
    public static string[] Lines(ReviewWalkthroughStore store) =>
        store.Exchange.Value.Select(Line).ToArray();

    /// <summary>The narrator's latest prose on the current exchange, or null when it wrote none.</summary>
    public static string? Narration(ReviewWalkthroughStore store) =>
        store.Exchange.Value.OfType<WalkthroughMessage.Narration>().LastOrDefault()?.Text.Value;

    private static string Line(WalkthroughMessage message) => message switch
    {
        WalkthroughMessage.Question q => $"You: {q.Text}",
        WalkthroughMessage.Narration n => n.Text.Value,
        WalkthroughMessage.Failure f => $"Failed: {f.Text}",
        WalkthroughMessage.Refusal r => $"Refused: {r.Text}",
        _ => throw new ArgumentOutOfRangeException(nameof(message), message, "Unknown message."),
    };
}
