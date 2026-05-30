using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Base for view models built around an immutable state record. Centralizes the patterns
/// that otherwise pile up in every VM: per-field <see cref="Derived{T}"/> slices over a
/// single <see cref="State{T}"/>, a generation-guarded background-op runner that posts
/// results back through an <see cref="IUiDispatcher"/>, a subscription bag for upstream
/// observables/messages, and disposal of all of the above.
///
/// Subclasses construct the initial state via the base ctor, declare slices with
/// <see cref="Slice"/> in ctor order (slice notification fires in subscription order, so
/// order here matters when downstream rendering is order-sensitive), mutate state through
/// <see cref="Update"/>, and route async work through <see cref="RunBackground"/>.
///
/// A VM with several independent streams of async work (e.g. a background load racing a
/// user-driven mutation) declares a <see cref="GenerationGuard"/> lane per concern with
/// <see cref="CreateLane"/> and passes it to <see cref="RunBackground"/>. Work in one lane
/// never invalidates an in-flight continuation in another, so a mutation can't silently drop
/// a concurrent reload (or vice versa). The default <see cref="Gen"/> lane covers VMs with a
/// single stream.
/// </summary>
internal abstract class ViewModelBase<TState> : IDisposable
{
    private readonly List<IDisposable> _slices = new();
    private readonly List<GenerationGuard> _lanes = new();

    protected IUiDispatcher Dispatcher { get; }
    protected SubscriptionGroup Subscriptions { get; } = new();
    protected GenerationGuard Gen { get; }
    protected State<TState> State { get; }

    protected ViewModelBase(IUiDispatcher dispatcher, TState initial)
    {
        Dispatcher = dispatcher;
        State = new State<TState>(initial);
        Gen = CreateLane();
    }

    /// <summary>
    /// Creates an independent generation lane registered for disposal-invalidation. Use a
    /// dedicated lane per concern (loads, index mutations, commit) and pass it to
    /// <see cref="RunBackground"/> so an op in one lane never drops an in-flight continuation
    /// in another. Must be called from the ctor body (after the base ctor has run).
    /// </summary>
    protected GenerationGuard CreateLane()
    {
        var lane = new GenerationGuard();
        _lanes.Add(lane);
        return lane;
    }

    /// <summary>
    /// Declares a per-field projection over <see cref="State"/>. Tracked for disposal in
    /// reverse construction order. Slices notify in the order they're created — declare
    /// dependents before consumers when the downstream view relies on apply ordering.
    /// </summary>
    protected IReadable<T> Slice<T>(Func<TState, T> selector)
    {
        var derived = new Derived<T>(() => selector(State.Value));
        _slices.Add(derived);
        return derived;
    }

    protected void Update(Func<TState, TState> reducer)
        => State.Value = reducer(State.Value);

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread; on completion, posts
    /// <paramref name="onResult"/> to the UI thread. The continuation is dropped if
    /// <paramref name="lane"/> (defaulting to <see cref="Gen"/>) has advanced since this call
    /// started — repo switched, newer op in the same lane started, or the VM was disposed — so
    /// stale results never clobber fresher state. Pass a dedicated lane (see
    /// <see cref="CreateLane"/>) to isolate a stream of work from unrelated ops. The work
    /// tuple lets callers report in-band errors without throwing; a thrown exception is
    /// captured as its <c>Message</c>.
    /// </summary>
    protected void RunBackground<T>(
        Func<(T? Result, string? Error)> work,
        Action<T?, string?> onResult,
        GenerationGuard? lane = null)
    {
        lane ??= Gen;
        var gen = lane.Bump();
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            T? result = default;
            string? errorMsg = null;
            try
            {
                (result, errorMsg) = work();
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            dispatcher.Post(() =>
            {
                if (lane.IsStale(gen)) return;
                onResult(result, errorMsg);
            });
        });
    }

    public virtual void Dispose()
    {
        // Bump every lane first so any in-flight worker that resolves after Dispose sees a
        // stale gen and exits without touching state or firing notifications.
        foreach (var lane in _lanes) lane.Bump();
        Subscriptions.Dispose();
        for (var i = _slices.Count - 1; i >= 0; i--)
            _slices[i].Dispose();
        _slices.Clear();
        // Drop any remaining subscribers on the owned State — slices unhook themselves above,
        // but direct subscribers (e.g. a view binding straight to State) rely on this to be
        // severed deterministically rather than waiting for the whole VM to be collected.
        State.Dispose();
    }
}
