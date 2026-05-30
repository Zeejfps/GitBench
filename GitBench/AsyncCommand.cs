using ZGF.Observable;

namespace GitGui;

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
    private readonly OperationRunner _runner;
    private readonly Func<string?> _work;
    private readonly Action _onSuccess;
    private readonly Action<string>? _onError;
    private readonly State<bool> _isRunning = new(false);
    private readonly State<string?> _error = new(null);

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
    public AsyncCommand(
        IUiDispatcher dispatcher,
        Func<string?> work,
        Action onSuccess,
        IReadable<bool>? gate = null,
        Action<string>? onError = null)
    {
        _runner = new OperationRunner(dispatcher);
        _work = work;
        _onSuccess = onSuccess;
        _onError = onError;

        CanExecute = gate is null
            ? new Derived<bool>(() => !_isRunning.Value)
            : new Derived<bool>(() => !_isRunning.Value && gate.Value);
    }

    public void Execute()
    {
        if (!CanExecute.Value) return;
        _error.Value = null;
        _isRunning.Value = true;
        _runner.Run(
            _work,
            ex => ex.Message,
            error =>
            {
                _isRunning.Value = false;
                if (error != null) { _error.Value = error; _onError?.Invoke(error); return; }
                _onSuccess();
            });
    }
}
