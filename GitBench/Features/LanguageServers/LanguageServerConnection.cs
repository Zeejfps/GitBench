using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Git;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

internal sealed class LanguageServerConnection : ILanguageServerProcess, ILanguageClient
{
    private readonly ILanguageServerSession _server;
    private readonly LanguageServerEntry _entry;
    private readonly AskAgainPolicy _retry;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Action<Action> _post;
    private readonly PreviewSession _session;
    private readonly CancellationTokenSource _closing = new();
    private readonly Task<string?> _handshake;
    private readonly object _gate = new();

    private Action<ServerExit>? _exited;
    private ServerExit? _ending;
    private int _disposed;

    public LanguageServerConnection(
        ILanguageServerSession server,
        ServerLaunchRequest request,
        TimeSpan handshakeTimeout,
        AskAgainPolicy? retry = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        Action<Action>? post = null)
    {
        _server = server;
        _entry = request.Entry;
        _retry = retry ?? AskAgainPolicy.Default;
        _wait = wait ?? Task.Delay;
        _post = post ?? (action => action());

        server.ReadinessChanged += OnReadinessChanged;
        server.Exited += OnExited;
        server.DiagnosticsPublished += OnDiagnosticsPublished;
        _session = new PreviewSession(this, BoundaryOf(request.RepoRoot));
        _session.StateChanged += state => DocumentChanged?.Invoke(state);
        _handshake = HandshakeAsync(handshakeTimeout);
    }

    public event Action<DocumentState>? DocumentChanged;

    public event Action<PublishedDiagnostics>? DiagnosticsPublished;

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

        var at = new LspPosition(LspLine.FromOneBased(line.Value), new LspCharacter(column.Value));
        var answer = await _session.HoverAsync(at).ConfigureAwait(false);
        return answer is HoverAnswer.Content content ? content.Text : null;
    }

    public async Task<DefinitionReply> DefinitionAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return DefinitionReply.Nothing;
        if (!AnswersDefinitions) return DefinitionReply.Nothing;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false))
            return DefinitionReply.Nothing;

        var at = new LspPosition(LspLine.FromOneBased(line.Value), new LspCharacter(column.Value));
        var answer = await _session.DefinitionAsync(at).ConfigureAwait(false);
        return answer is DefinitionAnswer.Targets targets
            ? new DefinitionReply(targets.Items, targets.Origin)
            : DefinitionReply.Nothing;
    }

    public bool AnswersDefinitions => _server.Capabilities is not { SupportsDefinition: false };

    public async Task PrepareAsync(string absolutePath, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return;
        if (!await EnsurePreviewedAsync(absolutePath, cancel).ConfigureAwait(false)) return;
        Probe(DocumentUri.OfFile(absolutePath));
    }

    public void StopPreview() => _session.Clear();

    bool ILanguageClient.Handles(LanguageId language) => _entry.Language.Equals(language);

    void ILanguageClient.OpenDocument(
        DocumentUri uri, LanguageId language, DocumentVersion version, string text) =>
        _ = _server.OpenAsync(uri, language, version, text, _closing.Token);

    void ILanguageClient.CloseDocument(DocumentUri uri) => _ = _server.CloseAsync(uri, _closing.Token);

    async Task<HoverReply> ILanguageClient.HoverAsync(
        DocumentUri uri, LspPosition position, CancellationToken cancel)
    {
        var response = await AskAgain
            .AskAsync(
                token => _server.AskAsync(LspRequests.Hover(uri, position), _entry.RequestTimeout, token),
                _retry,
                _wait,
                cancel)
            .ConfigureAwait(false);

        return response is LspResponse<Hover>.Ok(var hover) ? ToReply(hover) : Nothing;
    }

    async Task<DefinitionPayload> ILanguageClient.DefinitionAsync(
        DocumentUri uri, LspPosition position, CancellationToken cancel)
    {
        var response = await AskAgain
            .AskAsync(
                token => _server.AskAsync(LspRequests.Definition(uri, position), _entry.RequestTimeout, token),
                _retry,
                _wait,
                cancel)
            .ConfigureAwait(false);

        return response is LspResponse<Definition>.Ok(Definition.Targets targets)
            ? new DefinitionPayload.Links(targets.Items
                .Select(item => new LocationLink(
                    item.Uri,
                    item.EnclosingRange,
                    OptionalRange.Of(item.Range),
                    item.OriginRange is { } origin ? OptionalRange.Of(origin) : OptionalRange.Absent))
                .ToArray())
            : DefinitionPayload.Nothing;
    }

    private static RepoBoundary BoundaryOf(string repoRoot) =>
        RepoBoundary.At(
            [repoRoot, RealPath.Of(repoRoot)],
            OperatingSystem.IsLinux() ? PathComparison.CaseSensitive : PathComparison.CaseInsensitive);

    private static readonly HoverReply Nothing = new(HoverPayload.Nothing, OptionalRange.Absent);

    private static HoverReply ToReply(Hover hover) => hover switch
    {
        Hover.Text(var kind, var value, var range) => new HoverReply(
            new HoverPayload.Markup(kind, value),
            range is { } present ? OptionalRange.Of(present) : OptionalRange.Absent),
        _ => Nothing,
    };

    public void RequestShutdown() => _server.RequestShutdown();

    public void Kill() => _server.Kill();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _server.ReadinessChanged -= OnReadinessChanged;
        _server.Exited -= OnExited;
        _server.DiagnosticsPublished -= OnDiagnosticsPublished;
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

    private async Task<bool> EnsurePreviewedAsync(string absolutePath, CancellationToken cancel)
    {
        var uri = DocumentUri.OfFile(absolutePath);
        if (_session.State is DocumentState.Open open && open.Uri == uri) return true;

        string text;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists) return false;
            if (info.Length > FileContentLoader.MaxTextBytes)
            {
                _session.Preview(new PreviewFile(uri, _entry.Language, PreviewContent.Truncated));
                return false;
            }
            text = await File.ReadAllTextAsync(absolutePath, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        _session.Preview(new PreviewFile(uri, _entry.Language, PreviewContent.Whole(text)));
        return _session.State is DocumentState.Open;
    }

    private void OnDiagnosticsPublished(PublishedDiagnostics published) =>
        DiagnosticsPublished?.Invoke(published);

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
