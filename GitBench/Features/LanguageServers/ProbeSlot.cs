using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// One question out at a time: asking again cancels what was out, the wait and the question run
/// off the UI thread, and the answer comes back on it — or not at all, once the question has been
/// withdrawn.
/// </summary>
internal sealed class ProbeSlot : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _dwell;

    private CancellationTokenSource? _pending;

    public ProbeSlot(IUiDispatcher dispatcher, Func<TimeSpan, CancellationToken, Task>? dwell = null)
    {
        _dispatcher = dispatcher;
        _dwell = dwell ?? Task.Delay;
    }

    public void Ask<T>(TimeSpan dwell, Func<CancellationToken, Task<T>> ask, Action<T> then)
    {
        Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;
        _ = RunAsync(pending.Token, dwell, ask, then);
    }

    /// <summary>The wait alone: the decision about what to ask is made back on the UI thread.</summary>
    public void Wait(TimeSpan dwell, Action then) => Ask(dwell, _ => Task.FromResult(true), _ => then());

    public void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose() => Cancel();

    private async Task RunAsync<T>(
        CancellationToken token, TimeSpan dwell, Func<CancellationToken, Task<T>> ask, Action<T> then)
    {
        try
        {
            if (dwell > TimeSpan.Zero) await _dwell(dwell, token).ConfigureAwait(false);
            var answer = await ask(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            _dispatcher.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                then(answer);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguageServers] probe failed: {ex.Message}");
        }
    }
}
