using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Owns the toast stack and each toast's lifecycle. Mirrors the other stores' shape: a single
/// observable projection (<see cref="Active"/>) and a <see cref="Start"/> that wires the UI
/// dispatcher once the loop exists. Dismissal is two-phase: <see cref="Dismiss"/> flags the toast
/// "exiting" (its card observes this and plays an exit animation), then the toast is removed for
/// real after <see cref="ExitDuration"/>. Toasts are added/removed on the UI thread (bus broadcasts
/// and direct callers both run there), so the lists and tables need no locking.
/// </summary>
internal sealed class ToastService : IToastService, IDisposable
{
    // The stack is capped so a burst (e.g. fetch-all across many repos) can't bury the screen;
    // the oldest toast drops when a new one would exceed the cap.
    private const int MaxVisible = 4;

    // How long a toast stays mounted after Dismiss so its card can animate out before removal.
    // Matches the card's reverse-tween duration.
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(300);

    // Returned for ids the service no longer tracks (already removed); never flips, never disposed.
    private static readonly State<bool> NotExiting = new(false);

    private readonly IMessageBus _bus;
    private readonly State<IReadOnlyList<Toast>> _active = new(Array.Empty<Toast>());
    private readonly Dictionary<ToastId, CancellationTokenSource> _timers = new();
    private readonly Dictionary<ToastId, State<bool>> _exiting = new();
    private readonly Action<ShowToastMessage> _onShow;

    private IUiDispatcher? _dispatcher;
    private long _nextId;
    private bool _disposed;

    public IReadable<IReadOnlyList<Toast>> Active => _active;

    public ToastService(IMessageBus bus)
    {
        _bus = bus;
        _onShow = m => { if (m.Intent != null) Show(m.Intent); };
        _bus.Subscribe(_onShow);
    }

    /// <summary>Idempotent; wires the dispatcher used to post timed dismissals back to the UI thread.</summary>
    public void Start(IUiDispatcher dispatcher) => _dispatcher ??= dispatcher;

    public IReadable<bool> Exiting(ToastId id) => _exiting.TryGetValue(id, out var st) ? st : NotExiting;

    public ToastId Show(ToastIntent intent)
    {
        var id = new ToastId(_nextId++);
        if (_disposed) return id;

        _exiting[id] = new State<bool>(false);
        var next = new List<Toast>(_active.Value) { new(id, intent) };
        while (next.Count > MaxVisible)
        {
            // Over the cap: drop the oldest immediately (no exit animation for a capped-out toast).
            DiscardTracking(next[0].Id);
            next.RemoveAt(0);
        }
        _active.Value = next;

        if (intent.Lifetime is ToastLifetime.Timed timed)
            Schedule(id, timed.Duration, () => Dismiss(id));

        return id;
    }

    public void Dismiss(ToastId id)
    {
        if (_disposed) return;
        if (!_exiting.TryGetValue(id, out var exiting) || exiting.Value) return; // gone or already exiting

        // Cancel the auto-dismiss timer, flag the exit (the card reverses its tween), and remove for
        // real once the exit animation has had time to play.
        CancelTimer(id);
        exiting.Value = true;
        Schedule(id, ExitDuration, () => Remove(id));
    }

    private void Remove(ToastId id)
    {
        CancelTimer(id);

        // Update the list first — that synchronously disposes the card's view model and unmounts its
        // card (unsubscribing from the exit flag) — then dispose the flag.
        var current = _active.Value;
        var next = new List<Toast>(current.Count);
        foreach (var toast in current)
            if (toast.Id != id)
                next.Add(toast);
        if (next.Count != current.Count)
            _active.Value = next;

        DiscardTracking(id);
    }

    private void DiscardTracking(ToastId id)
    {
        CancelTimer(id);
        if (_exiting.Remove(id, out var st))
            st.Dispose();
    }

    // Mirrors the status-bar feedback linger: a background delay that posts an action back to the UI
    // thread, cancelled if a newer timer for the same id supersedes it.
    private void Schedule(ToastId id, TimeSpan delay, Action onElapsed)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) { onElapsed(); return; } // pre-Start: act immediately

        CancelTimer(id);
        var cts = new CancellationTokenSource();
        _timers[id] = cts;
        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                dispatcher.Post(() => { if (!token.IsCancellationRequested) onElapsed(); });
            }
            catch (OperationCanceledException) { /* superseded before the delay elapsed */ }
        }, token);
    }

    private void CancelTimer(ToastId id)
    {
        if (!_timers.Remove(id, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _bus.Unsubscribe(_onShow);
        foreach (var cts in _timers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _timers.Clear();
        foreach (var st in _exiting.Values)
            st.Dispose();
        _exiting.Clear();
        _active.Dispose();
    }
}
