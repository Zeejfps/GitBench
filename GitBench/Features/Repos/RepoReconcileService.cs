using GitBench.Messages;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Observable;

namespace GitBench.Features.Repos;

// Re-broadcasts the ordinary change channels for the active repo on a slow cadence, so no UI state
// can stay stale indefinitely when a filesystem signal never arrives at all — an FSW that failed to
// attach, a network share, a change made while the app was closed.
//
// It deliberately reuses WorkingTreeChangedMessage / RefsChangedMessage rather than adding a
// reconcile-specific message: a tick *is* "assume the watcher missed something on every channel",
// every subscriber already handles both idempotently, and they carry the warm-set fan-out. Not
// RepoRefreshRequestedMessage, which means explicit user retry after a failed load and nulls the
// local slice first — a background tick on that channel would blank the file list to a skeleton
// every interval.
internal sealed class RepoReconcileService : IHostedService, IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    // How long a postponed focus reconcile waits before re-testing the activity gate. Short, because
    // the whole point is to land promptly once git goes quiet, and the re-test is a flag read.
    private static readonly TimeSpan FocusRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IRepoRegistry _registry;
    private readonly IMessageBus _bus;
    private readonly IUiDispatcher _dispatcher;
    private readonly IRepoActivityTracker _activity;
    private readonly IAppForeground _foreground;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _foregroundSub;
    private bool _started;
    private bool _sawInitialForeground;
    // UI thread only, like everything Reconcile touches: set when a focus reconcile is postponed and
    // cleared by the retry itself, so a long git op can only ever have one retry outstanding.
    private bool _focusRetryQueued;
    private volatile bool _disposed;

    public RepoReconcileService(
        IRepoRegistry registry,
        IMessageBus bus,
        IUiDispatcher dispatcher,
        IRepoActivityTracker activity,
        IAppForeground foreground,
        TimeSpan interval)
    {
        _registry = registry;
        _bus = bus;
        _dispatcher = dispatcher;
        _activity = activity;
        _foreground = foreground;
        _interval = interval;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _foregroundSub = _foreground.Subscribe(OnForegroundChanged);
        _ = RunLoopAsync();
    }

    private void OnForegroundChanged(bool foreground)
    {
        // Subscribe fires immediately with the current value. That is startup, the one moment every
        // store has just loaded, so it is not a focus gain and must not reconcile.
        if (!_sawInitialForeground)
        {
            _sawInitialForeground = true;
            return;
        }
        if (foreground) _dispatcher.Post(ReconcileOnFocus);
    }

    // PeriodicTimer rather than IFrameTicker: the ticker's onActivated hook pins the render loop at
    // full frame rate for as long as anything is registered, and an idle focused window must not
    // redraw between reconciles.
    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                _dispatcher.Post(ReconcileTick);
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    // A tick and a focus gain are different events and must not behave alike.
    //
    // A tick is cheap liveness. Another is one interval away carrying identical information, so it
    // may be dropped outright when git is busy, and it stays on the two channels a single status
    // read already covers.
    //
    // A focus gain is one-shot: the app was away and may have missed anything at all, including
    // changes with no other route in — so it covers every channel, and a git op in flight may only
    // postpone it. Dropping one leaves the user looking at stale data until the next tick.
    private void ReconcileTick() => Reconcile(allChannels: false, deferIfBusy: false);

    private void ReconcileOnFocus() => Reconcile(allChannels: true, deferIfBusy: true);

    // Runs on the UI thread; every condition is re-read here rather than captured, so losing focus
    // or switching repos changes the next tick without tearing the loop down.
    private void Reconcile(bool allChannels, bool deferIfBusy)
    {
        if (_disposed || !_foreground.Value) return;
        if (_registry.Active.Value is not { } repo) return;
        // A repo whose reads take longer than the interval would otherwise accumulate a queue of
        // reconciles, each adding another pair of git reads to a disk already behind.
        if (_activity.IsActive(repo.Path))
        {
            if (deferIfBusy) ScheduleFocusRetry();
            return;
        }

        _bus.Broadcast(new WorkingTreeChangedMessage(repo.Id));
        _bus.Broadcast(new RefsChangedMessage(repo.Id));
        if (!allChannels) return;
        // Worktree and submodule discovery each cost their own git process, so they ride the rare
        // focus gain rather than every tick.
        _bus.Broadcast(new WorktreesChangedMessage(repo.Id));
        _bus.Broadcast(new SubmodulesChangedMessage(repo.Id));
    }

    private void ScheduleFocusRetry()
    {
        if (_focusRetryQueued) return;
        _focusRetryQueued = true;
        _ = RetryFocusReconcileAsync();
    }

    private async Task RetryFocusReconcileAsync()
    {
        try { await Task.Delay(FocusRetryDelay, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        _dispatcher.Post(() =>
        {
            _focusRetryQueued = false;
            ReconcileOnFocus();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _foregroundSub?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
