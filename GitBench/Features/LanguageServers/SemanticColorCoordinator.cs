using GitBench.Features.Diff;
using GitBench.Lsp.Documents;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// Keeps the file on screen colored by what its language server says the types in it are: asks
/// once the file is shown, again whenever the server has learned something, and publishes the
/// answer for the view to lay over the parser's colors.
/// </summary>
/// <remarks>
/// <para>
/// One question for the whole file, one out at a time, and a newer reason to ask replaces the one
/// in flight. A server's first answer can be short — asked before it has loaded the project, it
/// classifies only what the open file declares — so it is asked again on each state change and each
/// diagnostics wave, which a server only publishes once it has analysed the file.
/// </para>
/// <para>
/// An answer that could not be had leaves what is up alone. The colors are an addition to the
/// parser's, so a refusal from a busy server is no reason to take back what an earlier answer said;
/// only a different file clears them.
/// </para>
/// </remarks>
internal sealed class SemanticColorCoordinator : IDisposable
{
    // Long enough that a burst of edits and the diagnostics wave behind them ask once, short enough
    // that a file opened reads in its final colors before the reader has found their place.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly ISemanticTokenSource _servers;
    private readonly Func<string?> _document;
    private readonly Action<SemanticColorOverlay> _publish;
    private readonly ProbeSlot _asking;

    private string? _path;
    private int _disposed;

    public SemanticColorCoordinator(
        ISemanticTokenSource servers,
        IUiDispatcher dispatcher,
        Func<string?> document,
        Action<SemanticColorOverlay> publish,
        Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        _servers = servers;
        _document = document;
        _publish = publish;
        _asking = new ProbeSlot(dispatcher, settle);
    }

    /// <summary>Asks about the file on screen, after it has settled. On the UI thread.</summary>
    public void Refresh()
    {
        if (_disposed != 0) return;

        var path = _document();
        if (path != _path)
        {
            _asking.Cancel();
            _path = path;
            _publish(SemanticColorOverlay.Empty);
        }

        if (path is null || !_servers.CanClassify(path)) return;

        _asking.Ask(
            Settle,
            async cancel => Overlay(path, await _servers.SemanticTokensAsync(path, cancel).ConfigureAwait(false)),
            overlay =>
            {
                if (_disposed != 0 || path != _path || overlay is null) return;
                _publish(overlay);
            });
    }

    /// <summary>Built off the UI thread: it walks every token in the file.</summary>
    private static SemanticColorOverlay? Overlay(string path, SemanticTokensReply reply) => reply switch
    {
        SemanticTokensReply.Answered answered => SemanticColorOverlay.Of(path, answered.Text, answered.Tokens),
        SemanticTokensReply.Unavailable => null,
        _ => throw new NotSupportedException($"unhandled semantic tokens reply {reply.GetType().Name}"),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _asking.Dispose();
    }
}
