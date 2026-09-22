using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

internal sealed class LanguageServerConnection : ILanguageServerProcess
{
    /// <summary>How long the text has to sit still before the server is told about it again.</summary>
    private static readonly TimeSpan ResyncDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILanguageServerSession _server;
    private readonly LanguageServerEntry _entry;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Action<Action> _post;
    private readonly IFileTextSource _files;
    private readonly PreviewSession _session;
    private readonly CancellationTokenSource _closing = new();
    private readonly Task<string?> _handshake;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _previewing = new(1, 1);

    private Action<ServerExit>? _exited;
    private ServerExit? _ending;
    private int _disposed;
    private int _stale;
    private int _resyncs;

    public LanguageServerConnection(
        ILanguageServerSession server,
        ServerLaunchRequest request,
        TimeSpan handshakeTimeout,
        AskAgainPolicy? retry = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        Action<Action>? post = null,
        IFileTextSource? files = null)
    {
        _server = server;
        _entry = request.Entry;
        _wait = wait ?? Task.Delay;
        _post = post ?? (action => action());
        _files = files ?? FilesOnDisk.Instance;

        server.ReadinessChanged += OnReadinessChanged;
        server.Exited += OnExited;
        _files.Changed += OnFileTextChanged;
        _session = new PreviewSession(
            server, _entry, BoundaryOf(request.RepoRoot), retry ?? AskAgainPolicy.Default, _wait);
        _session.StateChanged += state => DocumentChanged?.Invoke(state);
        _handshake = HandshakeAsync(handshakeTimeout);
    }

    public event Action<DocumentState>? DocumentChanged;

    public DocumentState Document => _session.State;

    public event Action<ServerReadiness>? ReadinessChanged;

    public event Action<ServerExit>? Exited
    {
        add
        {
            ServerExit? already;
            lock (_gate)
            {
                already = _ending;
                if (already is null) _exited += value;
            }

            if (already is { } exit) value?.Invoke(exit);
        }
        remove
        {
            lock (_gate) _exited -= value;
        }
    }

    public async Task<HoverText?> HoverAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return null;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false)) return null;

        return await _session.HoverAsync(At(line, column)).ConfigureAwait(false);
    }

    public async Task<DefinitionReply> DefinitionAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return DefinitionReply.Nothing;
        if (!AnswersDefinitions) return DefinitionReply.Nothing;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false))
            return DefinitionReply.Nothing;

        return await _session.DefinitionAsync(At(line, column)).ConfigureAwait(false);
    }

    public bool AnswersDefinitions => _server.Capabilities is not { SupportsDefinition: false };

    public async Task<ReferenceReply> ReferencesAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return ReferenceReply.Unavailable.Instance;
        if (!AnswersReferences) return ReferenceReply.Unavailable.Instance;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false))
            return ReferenceReply.Unavailable.Instance;

        return await _session.ReferencesAsync(At(line, column)).ConfigureAwait(false);
    }

    public bool AnswersReferences => _server.Capabilities is not { SupportsReferences: false };

    public async Task<SemanticTokensReply> SemanticTokensAsync(string absolutePath, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return SemanticTokensReply.Unavailable.Instance;
        if (_server.Capabilities?.SemanticTokens is not SemanticTokensSupport.WholeDocument(var legend))
            return SemanticTokensReply.Unavailable.Instance;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false))
            return SemanticTokensReply.Unavailable.Instance;

        return await _session.SemanticTokensAsync(legend).ConfigureAwait(false);
    }

    public bool AnswersSemanticTokens => _server.Capabilities is not { SemanticTokens: SemanticTokensSupport.None };

    public async Task PrepareAsync(string absolutePath, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false)) return;
        Probe(DocumentUri.OfFile(absolutePath));
    }

    public void StopPreview() => _session.Clear();

    private static LspPosition At(FileLine line, RawColumn column) =>
        new(LspLine.FromOneBased(line.Value), new LspCharacter(column.Value));

    private static RepoBoundary BoundaryOf(string repoRoot) =>
        RepoBoundary.At(
            [repoRoot, RealPath.Of(repoRoot)],
            OperatingSystem.IsLinux() ? PathComparison.CaseSensitive : PathComparison.CaseInsensitive);

    public void RequestShutdown() => _server.RequestShutdown();

    public void Kill() => _server.Kill();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _server.ReadinessChanged -= OnReadinessChanged;
        _server.Exited -= OnExited;
        _files.Changed -= OnFileTextChanged;
        lock (_gate) _exited = null;
        DocumentChanged = null;
        _closing.Cancel();
        _session.Dispose();
        _server.Dispose();
        _closing.Dispose();
    }

    private Task<string?> Handshaked() => _handshake;

    private async Task<string?> HandshakeAsync(TimeSpan timeout)
    {
        string? failure;
        try
        {
            failure = await _server.HandshakeAsync(timeout, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "the connection was closed during startup.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is null) return null;

        End(new ServerExit(ExitCode: null, Detail: failure));
        _server.RequestShutdown();
        return failure;
    }

    /// <summary>
    /// Puts the file in front of the server before it is asked about it, and keeps what the server
    /// holds equal to what the reader is looking at.
    /// </summary>
    private async Task<bool> EnsurePreviewedAsync(string absolutePath, CancellationToken cancel)
    {
        var uri = DocumentUri.OfFile(absolutePath);
        if (Showing(uri) && Volatile.Read(ref _stale) == 0) return true;

        await _previewing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _stale, 0);
            var showing = Showing(uri);
            switch (await _files.ReadAsync(absolutePath, cancel).ConfigureAwait(false))
            {
                case CurrentText.Complete complete:
                    _session.Preview(new PreviewFile(uri, PreviewContent.Whole(complete.Text)));
                    return _session.State is DocumentState.Open;
                case CurrentText.CutShort:
                    _session.Preview(new PreviewFile(uri, PreviewContent.Truncated));
                    return false;
                case CurrentText.Unavailable:
                    return showing;
                case var other:
                    throw new NotSupportedException($"unhandled file text {other.GetType().Name}");
            }
        }
        finally
        {
            _previewing.Release();
        }
    }

    private bool Showing(DocumentUri uri) =>
        _session.State is DocumentState.Open open && open.Uri == uri;

    private void OnFileTextChanged(string absolutePath)
    {
        var uri = DocumentUri.OfFile(absolutePath);
        if (!Showing(uri)) return;

        Volatile.Write(ref _stale, 1);
        var generation = Interlocked.Increment(ref _resyncs);
        _ = ResyncAsync(absolutePath, generation);
    }

    /// <summary>Reopens the document against the text as it now stands, once the edits stop.</summary>
    private async Task ResyncAsync(string absolutePath, int generation)
    {
        try
        {
            await _wait(ResyncDelay, _closing.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _resyncs) != generation) return;
            await EnsurePreviewedAsync(absolutePath, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Probe(DocumentUri uri)
    {
        _ = AskAgain.AskAsync(
            ct => _server.AskAsync(
                LspRequests.Hover(uri, LspPosition.At(0, 0)), _entry.RequestTimeout, ct),
            ReadinessProbe,
            Task.Delay,
            _closing.Token);
    }

    /// <summary>
    /// The question that turns "indexing" into "ready". It keeps asking for as long as the server
    /// keeps refusing, because a refusal *is* the server saying it is still working — and a probe
    /// that gives up first leaves a finished server reading as still indexing until someone
    /// happens to hover over something. The ceiling is far past any measured cold start; a server
    /// that goes quiet altogether is the supervisor's problem, not this one's.
    /// </summary>
    private static readonly AskAgainPolicy ReadinessProbe = new()
    {
        MaxAttempts = 40,
        FirstDelay = TimeSpan.FromMilliseconds(500),
        MaxDelay = TimeSpan.FromSeconds(8),
    };

    private void OnReadinessChanged(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

    private void OnExited(ServerExit exit) => End(exit);

    /// <summary>
    /// Ends the connection once, on the thread the supervisor runs on. The handshake fails on
    /// whichever pool thread was awaiting it, and what listens to this ends up rebuilding views —
    /// raising it there tears the view tree down underneath the input system mid-frame.
    /// </summary>
    private void End(ServerExit exit)
    {
        Action<ServerExit>? listeners;
        lock (_gate)
        {
            if (_ending is not null) return;
            _ending = exit;
            listeners = _exited;
            _exited = null;
        }

        if (listeners is not null) _post(() => listeners(exit));
    }
}
