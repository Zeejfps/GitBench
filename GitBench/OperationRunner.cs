using ZGF.Observable;

namespace GitGui;

internal sealed class OperationRunner
{
    private readonly IUiDispatcher _dispatcher;
    private bool _isRunning;

    public OperationRunner(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool IsRunning => _isRunning;

    public void Run<TOutcome>(
        Func<TOutcome> work,
        Func<Exception, TOutcome> onException,
        Action<TOutcome> onResult)
    {
        if (_isRunning) return;
        _isRunning = true;

        var dispatcher = _dispatcher;

        Task.Run(() =>
        {
            TOutcome outcome;
            try { outcome = work(); }
            catch (Exception ex) { outcome = onException(ex); }

            dispatcher.Post(() =>
            {
                _isRunning = false;
                onResult(outcome);
            });
        });
    }
}
