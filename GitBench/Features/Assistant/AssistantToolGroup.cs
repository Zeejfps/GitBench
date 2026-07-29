using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// A run of tool calls the model made back to back, folded into one transcript entry. A turn that
/// reads six files says so on one line instead of six, and the answer stays on screen.
/// </summary>
/// <remarks>
/// The run ends at the first row that is not a tool call, so a group never spans an answer. Whether
/// it is open belongs here rather than to the row that draws it: the overlay is non-modal and can be
/// closed and reopened, and a streamed delta rebuilds nothing but the row it lands on.
///
/// The calls are the session's rows and are disposed with it — this owns only its own observables.
/// </remarks>
internal sealed class AssistantToolGroup : IDisposable
{
    private readonly State<bool> _expanded = new(false);
    private readonly Derived<int> _failed;
    private readonly Derived<bool> _running;
    private readonly Derived<bool> _single;

    public AssistantToolGroup()
    {
        _failed = new Derived<int>(() => Calls.Count(call => call.Failed.Value));
        _running = new Derived<bool>(() => Calls.Any(call => call.IsRunning.Value));
        _single = new Derived<bool>(() => Calls.Count == 1);
        Toggle = new Command(() => _expanded.Value = !_expanded.Value);
    }

    public ObservableList<AssistantRow> Calls { get; } = new();

    /// <summary>Collapsed until the reader asks, including while the run is still growing.</summary>
    public IReadable<bool> IsExpanded => _expanded;

    /// <summary>True while the run is one call, which reads as the single line it always was rather
    /// than as a group of one.</summary>
    public IReadable<bool> IsSingle => _single;

    /// <summary>How many of the calls failed, so a collapsed group still carries the signal instead
    /// of looking calm over a failure.</summary>
    public IReadable<int> FailedCount => _failed;

    public IReadable<bool> IsRunning => _running;

    public ICommand Toggle { get; }

    public void Add(AssistantRow row) => Calls.Add(row);

    public void Dispose()
    {
        _single.Dispose();
        _running.Dispose();
        _failed.Dispose();
        _expanded.Dispose();
    }
}
