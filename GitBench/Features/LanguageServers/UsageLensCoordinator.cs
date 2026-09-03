using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// Keeps the usages rows of the file on screen filled in: decides which declarations are worth
/// asking a language server about, asks, and publishes the answers for the rows to draw.
/// </summary>
/// <remarks>
/// <para>
/// Only what the reader can see is asked about. A file has far more declarations than fit on a
/// screen and a server answers about a symbol far more slowly than a reader scrolls past it, so
/// asking about the whole file would spend a server's afternoon on rows nobody looked at.
/// </para>
/// <para>
/// Answers are held against the declaration's containment path rather than its line, so the
/// reconcile tick — which re-reads the open file twice a minute — costs nothing: the same
/// declarations come back with the same ids and the counts are already known. An edit that moves a
/// declaration keeps its count; one that renames it gives it a new id, which asks again.
/// </para>
/// <para>
/// Everything here runs on the UI thread except the requests themselves, which is what makes the
/// bookkeeping safe without a lock: <see cref="Refresh"/> is called from UI events and every answer
/// comes back through the dispatcher before it is recorded.
/// </para>
/// </remarks>
internal sealed class UsageLensCoordinator : IDisposable
{
    // Long enough that flinging through a file asks about nothing it passes, short enough that
    // stopping to read is answered without the reader wondering whether it is broken.
    private const int SettleMs = 150;

    // Enough to fill a screen promptly, few enough that a server answering slowly is still
    // answering the reader's questions rather than a backlog of them.
    private const int AtOnce = 4;

    // How many times one declaration is asked about before its answer is taken as final. See
    // Recheck: the first answer can be short, and the last thing this should do is keep asking.
    private const int MaxAsks = 3;

    private readonly IReferenceSource _servers;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string?> _document;
    private readonly Func<IReadOnlyList<UsageLensTarget>> _onScreen;
    private readonly Func<IReadOnlyList<UsageLensTarget>> _everywhere;
    private readonly Action<bool> _showRows;
    private readonly Action<UsageLensOverlay> _publish;
    private readonly Func<TimeSpan, CancellationToken, Task> _settle;

    private readonly Dictionary<string, UsageLensState> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _asks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(AtOnce, AtOnce);

    private string? _path;
    private bool? _rowsShown;
    // Two lifetimes, because they end for different reasons. The wait before asking is abandoned
    // every time the view moves; the questions themselves outlive that and end only when the file
    // does — cancelling those on each scroll would mean a reader who keeps scrolling never gets an
    // answer to anything.
    private CancellationTokenSource? _settling;
    private CancellationTokenSource _asking = new();
    private int _disposed;

    public UsageLensCoordinator(
        IReferenceSource servers,
        IUiDispatcher dispatcher,
        Func<string?> document,
        Func<IReadOnlyList<UsageLensTarget>> onScreen,
        Func<IReadOnlyList<UsageLensTarget>> everywhere,
        Action<bool> showRows,
        Action<UsageLensOverlay> publish,
        Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        _servers = servers;
        _dispatcher = dispatcher;
        _document = document;
        _onScreen = onScreen;
        _everywhere = everywhere;
        _showRows = showRows;
        _publish = publish;
        _settle = settle ?? Task.Delay;
    }

    /// <summary>
    /// Reconsiders what is worth asking. Called for everything that changes which rows are on
    /// screen — a different file, a scroll, a fold opening — because all three change the answer
    /// and none of them is worth telling apart.
    /// </summary>
    public void Refresh()
    {
        if (_disposed != 0) return;

        if (_document() is not { } path)
        {
            Forget();
            return;
        }

        if (path != _path)
        {
            Forget();
            _path = path;
        }

        // Asked again every time rather than once per file: before a server has started this is
        // optimistically true, and the row it reserves is only honest once the server has said so.
        var canAsk = _servers.CanReference(path);
        ShowRows(canAsk);
        if (!canAsk)
        {
            _known.Clear();
            _asks.Clear();
            _publish(UsageLensOverlay.Empty);
            return;
        }

        StopSettling();
        var settling = new CancellationTokenSource();
        _settling = settling;
        _ = SettleThenAskAsync(path, settling.Token, _asking.Token);
    }

    private async Task SettleThenAskAsync(string path, CancellationToken settling, CancellationToken asking)
    {
        try
        {
            await _settle(TimeSpan.FromMilliseconds(SettleMs), settling).ConfigureAwait(false);
            if (settling.IsCancellationRequested || asking.IsCancellationRequested) return;

            var targets = Unanswered(_onScreen());
            if (targets.Count == 0) return;

            foreach (var target in targets)
            {
                _inFlight.Add(target.Id);
                _known[target.Id] = new UsageLensState.Asking();
            }

            Publish();
            foreach (var target in targets) _ = AskOneAsync(path, target, asking);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguageServers] usage lens refresh failed: {ex.Message}");
        }
    }

    private async Task AskOneAsync(string path, UsageLensTarget target, CancellationToken cancel)
    {
        UsageLensState? state = null;
        try
        {
            await _slots.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                var reply = await _servers
                    .ReferencesAsync(path, target.NameLine, target.NameColumn, cancel)
                    .ConfigureAwait(false);

                // "Could not ask" is recorded rather than dropped, so the row says so instead of
                // staying blank — and the next refresh asks again, because a server that was still
                // starting answers the second time.
                state = reply switch
                {
                    ReferenceReply.Answered answered => new UsageLensState.Count(answered.Sites.Count),
                    ReferenceReply.Unavailable => new UsageLensState.Unsupported(),
                    _ => throw new NotSupportedException($"unhandled reference reply {reply.GetType().Name}"),
                };
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguageServers] usage lens question failed: {ex.Message}");
        }

        // Always lands, answer or not: a question abandoned has to stop counting as outstanding,
        // or the row it belongs to would sit on "asking" for as long as the file stayed open.
        _dispatcher.Post(() =>
        {
            if (path != _path) return;
            _inFlight.Remove(target.Id);
            if (state is null)
            {
                _known.Remove(target.Id);
            }
            else
            {
                _known[target.Id] = state;
                _asks[target.Id] = _asks.GetValueOrDefault(target.Id) + 1;
            }

            Publish();
        });
    }

    /// <summary>
    /// Says a server has told us something new, so what it said before is worth asking again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server's first answer is not always its best one. Asked before it has loaded the project
    /// around a file, a server answers about the one file it has read — which is a plausible number
    /// rather than an obvious failure, and the reader has no way to tell it is short. So a count is
    /// re-asked when the servers report progress, not held from the first reply.
    /// </para>
    /// <para>
    /// Bounded, and hung off this rather than off the ordinary refresh, which runs on every scroll:
    /// a server that is failing refuses instantly, and one that publishes diagnostics every few
    /// seconds would otherwise be asked about every declaration on screen for as long as the file
    /// stayed open.
    /// </para>
    /// </remarks>
    public void Recheck()
    {
        if (_disposed != 0) return;

        foreach (var id in _known.Keys.ToArray())
            if (_asks.GetValueOrDefault(id) < MaxAsks) _known.Remove(id);

        Refresh();
    }

    /// <summary>The declarations on screen that nothing is known about and nothing is out asking
    /// about.</summary>
    private List<UsageLensTarget> Unanswered(IReadOnlyList<UsageLensTarget> targets)
    {
        var asking = new List<UsageLensTarget>();
        foreach (var target in targets)
        {
            if (_inFlight.Contains(target.Id)) continue;
            if (_known.ContainsKey(target.Id)) continue;
            asking.Add(target);
        }

        return asking;
    }

    /// <summary>
    /// Publishes what is known for every row in the file, not only the rows that prompted the
    /// asking: a count answered while its declaration was on screen has to still be there when the
    /// reader scrolls back to it.
    /// </summary>
    private void Publish()
    {
        if (_path is not { } path) return;

        var states = new Dictionary<FileLine, UsageLensState>();
        foreach (var target in _everywhere())
            if (_known.TryGetValue(target.Id, out var state)) states[target.At] = state;

        _publish(new UsageLensOverlay(path, states));
    }

    /// <summary>
    /// Says whether the file should carry usages rows at all, and only when the answer changes.
    /// </summary>
    /// <remarks>
    /// Handed over rather than called straight through, because growing or dropping the rows
    /// re-flattens the file and one of the things that reaches this is a scroll — which is raised
    /// while the list is drawing the very rows the re-flatten would replace.
    /// </remarks>
    private void ShowRows(bool show)
    {
        if (_rowsShown == show) return;
        _rowsShown = show;
        _dispatcher.Post(() => _showRows(show));
    }

    private void Forget()
    {
        StopSettling();
        StopAsking();
        _known.Clear();
        _asks.Clear();
        _inFlight.Clear();
        _path = null;
        _publish(UsageLensOverlay.Empty);
    }

    private void StopSettling()
    {
        _settling?.Cancel();
        _settling?.Dispose();
        _settling = null;
    }

    private void StopAsking()
    {
        _asking.Cancel();
        _asking.Dispose();
        _asking = new CancellationTokenSource();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopSettling();
        _asking.Cancel();
        _asking.Dispose();
        // The gate is deliberately not disposed. Questions already through it are still unwinding,
        // and disposing it under them turns an ordinary cancellation into a thrown object-disposed;
        // it holds no wait handle, so there is nothing to release.
    }
}
