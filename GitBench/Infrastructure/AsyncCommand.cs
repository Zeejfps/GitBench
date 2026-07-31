using ZGF.Observable;

namespace GitBench.Infrastructure;

/// <summary>
/// Command that runs its work on a background thread, exposing observable
/// <see cref="IsRunning"/> and <see cref="Error"/> alongside the standard
/// <see cref="ICommand"/> surface. Replaces the recurring presenter pattern of: disable
/// button, clear error, dispatch to background, re-enable on failure / fire side effects
/// on success.
///
/// <see cref="CanExecute"/> already composes <see cref="IsRunning"/> with the optional
/// caller-supplied gate, so a bound button disables itself during execution without any
/// per-VM bookkeeping.
/// </summary>
internal sealed class AsyncCommand : ICommand
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string?> _work;
    private readonly Action _onSuccess;
    private readonly Action<string>? _onError;
    private readonly Func<Action<bool>?>? _onStart;
    private readonly State<bool> _isRunning = new(false);
    private readonly State<string?> _error = new(null);

    // The completion half of the current execution's onStart bracket. Held rather than passed
    // through the work delegate so it is invoked on the UI thread, alongside every other completion
    // effect. CanExecute gates on IsRunning, so there is only ever one execution to track.
    private Action<bool>? _settle;

    public IReadable<bool> CanExecute { get; }
    public IReadable<bool> IsRunning => _isRunning;
    public IReadable<string?> Error => _error;

    /// <param name="work">Runs on a background thread. Return null on success or an error
    /// string on failure. Exceptions are caught and surfaced as <see cref="Error"/> too.</param>
    /// <param name="onSuccess">Invoked on the UI thread after a null/no-error result.
    /// Typical use: broadcast bus messages and raise <c>CloseRequested</c>.</param>
    /// <param name="gate">Additional CanExecute condition ANDed with <see cref="IsRunning"/>
    /// being false. Pass null to gate purely on running state.</param>
    /// <param name="onError">Invoked on the UI thread after a failing result, with the same
    /// message already published to <see cref="Error"/>. Lets a VM react to failure — toggle a
    /// retry mode, close and route the error to a dialog — beyond the default of surfacing
    /// <see cref="Error"/>. Pass null to leave failure handling to <see cref="Error"/> alone.</param>
    /// <param name="onStart">Invoked on the UI thread when execution begins, before the work is
    /// dispatched, and may return a completion callback invoked — also on the UI thread — with
    /// whether the work succeeded. For state the rest of the app must see for exactly the command's
    /// lifetime, such as declaring where HEAD is about to move: waiting for the result would leave
    /// the window this exists to close. Return null to declare nothing for this execution.</param>
    public AsyncCommand(
        IUiDispatcher dispatcher,
        Func<string?> work,
        Action onSuccess,
        IReadable<bool>? gate = null,
        Action<string>? onError = null,
        Func<Action<bool>?>? onStart = null)
    {
        _dispatcher = dispatcher;
        _work = work;
        _onSuccess = onSuccess;
        _onError = onError;
        _onStart = onStart;

        CanExecute = gate is null
            ? new Derived<bool>(() => !_isRunning.Value)
            : new Derived<bool>(() => !_isRunning.Value && gate.Value);
    }

    /// <summary>
    /// Outcome-typed entry point: <paramref name="work"/> returns a result hierarchy and the
    /// command folds its failure case (or a thrown exception) into <see cref="Error"/>, so
    /// call sites pass the service call directly instead of adapting it back to a string.
    /// </summary>
    public static AsyncCommand ForOutcome<T>(
        IUiDispatcher dispatcher,
        Func<T> work,
        Action onSuccess,
        IReadable<bool>? gate = null,
        Action<string>? onError = null,
        Func<Action<bool>?>? onStart = null)
        where T : IOutcome<T>
        => new(dispatcher, () => work().FailureMessage, onSuccess, gate, onError, onStart);

    public void Execute()
    {
        if (!CanExecute.Value) return;
        _error.Value = null;
        _isRunning.Value = true;
        _settle = _onStart?.Invoke();

        Task.Run(() =>
        {
            string? error;
            try { error = _work(); }
            catch (Exception ex) { error = ex.Message; }
            _dispatcher.Post(() => Complete(error));
        });
    }

    private void Complete(string? error)
    {
        _isRunning.Value = false;
        // Before the success/error effects: those broadcast, and a listener reading the state the
        // bracket holds should see it already settled.
        var settle = _settle;
        _settle = null;
        settle?.Invoke(error is null);
        if (error is null) { _onSuccess(); return; }
        _error.Value = error;
        _onError?.Invoke(error);
    }
}
