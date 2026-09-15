using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Infrastructure;

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
///
/// Lanes are about staleness only, never about lifetime: disposal is a flag every posted
/// continuation checks. Git mutations have the opposite staleness semantics from loads and go
/// through <see cref="RunMutation"/>, which has no lane at all.
/// </summary>
internal abstract class ViewModelBase<TState> : IDisposable
{
    private readonly List<IDisposable> _slices = new();
    private bool _disposed;

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
    /// Creates an independent generation lane. Use a dedicated lane per concern (a load, a
    /// highlight pass, a lazy fetch) and pass it to <see cref="RunBackground"/> so an op in one
    /// lane never drops an in-flight continuation in another. An exclusive lane refuses to start
    /// a second op while one is in flight instead of superseding it.
    /// </summary>
    protected GenerationGuard CreateLane(bool exclusive = false) => new(exclusive);

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
    /// stale results never clobber fresher state. A thrown exception folds into the outcome's
    /// own failure case. On an exclusive lane, returns false without starting while a previous
    /// op is still in flight; the in-flight flag clears when the continuation posts even if the
    /// result went stale, so a dropped result can never wedge the lane shut.
    ///
    /// This is load semantics: a newer answer to the same question supersedes an older one. A git
    /// mutation is not a load and must not run here — see <see cref="RunMutation"/>.
    /// </summary>
    protected bool RunBackground<T>(Func<T> work, Action<T> onResult, GenerationGuard? lane = null)
        where T : IOutcome<T>
    {
        lane ??= Gen;
        if (lane.Exclusive && lane.InFlight) return false;
        lane.InFlight = true;
        var gen = lane.Bump();
        Dispatch(work, outcome =>
        {
            lane.InFlight = false;
            if (_disposed || lane.IsStale(gen)) return;
            onResult(outcome);
        });
        return true;
    }

    /// <summary>
    /// Runs a git mutation: work whose result must always be delivered. Two things differ from
    /// <see cref="RunBackground"/>, both because a mutation is not a load.
    ///
    /// There is no generation lane. Superseding a mutation is meaningless — the git process already
    /// ran and the index already moved — and dropping its continuation drops the only carrier of its
    /// error message and of the broadcast that reconciles the optimistically-updated lists. Only
    /// disposal stops delivery.
    ///
    /// The revalidation is the runner's job, not the callback's. <paramref name="effects"/> names the
    /// channels the op may have moved; it is broadcast once <paramref name="onResult"/> has settled
    /// this VM's own state, in a <c>finally</c>, so neither an early return nor a throwing
    /// continuation can leave the rest of the app unaware that the repo moved. It is broadcast even
    /// when this VM has been disposed — the revalidation is owed to the stores, not to the panel that
    /// happened to start the op.
    /// </summary>
    protected void RunMutation<T>(MutationEffects effects, Func<T> work, Action<T> onResult)
        where T : IOutcome<T>
        => Dispatch(work, outcome =>
        {
            try
            {
                if (!_disposed) onResult(outcome);
            }
            finally
            {
                effects.Broadcast();
            }
        });

    private void Dispatch<T>(Func<T> work, Action<T> continuation)
        where T : IOutcome<T>
    {
        var dispatcher = Dispatcher;
        Task.Run(() =>
        {
            T outcome;
            try
            {
                outcome = work();
            }
            catch (Exception ex)
            {
                outcome = T.Fail(ex.Message);
            }

            dispatcher.Post(() => continuation(outcome));
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;

        Subscriptions.Dispose();
        
        for (var i = _slices.Count - 1; i >= 0; i--)
        {
            _slices[i].Dispose();
        }
        _slices.Clear();
        
        State.Dispose();
    }
}
