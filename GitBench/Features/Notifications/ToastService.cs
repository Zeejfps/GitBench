using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Notifications;

/// <summary>
/// Owns the toast stack and each toast's expiry timer. Mirrors the other stores' shape: a single
/// observable projection (<see cref="Active"/>) and a <see cref="Start"/> that wires the UI
/// dispatcher once the loop exists. Toasts are added on the UI thread (bus broadcasts and direct
/// callers both run there), so the list and timer table need no locking.
/// </summary>
internal sealed class ToastService : IToastService, IDisposable
{
    // The stack is capped so a burst (e.g. fetch-all across many repos) can't bury the screen;
    // the oldest toast drops when a new one would exceed the cap.
    private const int MaxVisible = 4;

    private readonly IMessageBus _bus;
    private readonly State<IReadOnlyList<Toast>> _active = new(Array.Empty<Toast>());
    private readonly Dictionary<ToastId, CancellationTokenSource> _timers = new();
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

    public ToastId Show(ToastIntent intent)
    {
        var id = new ToastId(_nextId++);
        if (_disposed) return id;

        var next = new List<Toast>(_active.Value) { new(id, intent) };
        while (next.Count > MaxVisible)
        {
            CancelTimer(next[0].Id);
            next.RemoveAt(0);
        }
        _active.Value = next;

        if (intent.Lifetime is ToastLifetime.Timed timed)
            ScheduleDismiss(id, timed.Duration);

        return id;
    }

    public void Dismiss(ToastId id)
    {
        CancelTimer(id);

        var current = _active.Value;
        var next = new List<Toast>(current.Count);
        foreach (var toast in current)
            if (toast.Id != id)
                next.Add(toast);

        if (next.Count != current.Count)
            _active.Value = next;
    }

    // Mirrors the status-bar feedback linger: a background delay that posts the dismissal back to
    // the UI thread, cancelled if the toast is removed (capped out / dismissed) before it fires.
    private void ScheduleDismiss(ToastId id, TimeSpan delay)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;

        var cts = new CancellationTokenSource();
        _timers[id] = cts;
        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                dispatcher.Post(() => { if (!token.IsCancellationRequested) Dismiss(id); });
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
        _active.Dispose();
    }
}
