using System.Diagnostics;
using System.Text.Json;
using GitBench.Lsp.Documents;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// Starts language servers as real child processes.
/// </summary>
/// <remarks>
/// <para>
/// The process holds the only handle to the server's input. That is load-bearing: every server
/// measured exits on its input closing, which is what stops a crash of this app leaving a
/// multi-gigabyte indexer behind. Nothing may be interposed that keeps the pipe open.
/// </para>
/// <para>
/// Events reach the supervisor through <paramref name="post"/>, because a process exits and a
/// server reports progress on whatever thread the runtime chooses, and the supervisor holds no lock.
/// </para>
/// </remarks>
public sealed class ProcessLanguageServerLauncher(
    IServerEnvironment environment,
    Action<Action> post,
    TimeProvider? time = null) : ILanguageServerLauncher
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LaunchResult Launch(ServerLaunchRequest request)
    {
        var entry = request.Entry;
        if (environment.ResolveCommand(entry.Command) is not { } executable)
            return new LaunchResult.Failed($"'{entry.Command}' was not found.");

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.ProjectRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // A list, never a joined string: an argument containing a space or a quote is an argument,
        // not a chance to run something else.
        foreach (var argument in entry.Args) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment.Variables) start.Environment[key] = value;
        foreach (var (key, value) in entry.Environment) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LaunchResult.Failed($"'{entry.Command}' could not be started: {ex.Message}");
        }

        return new LaunchResult.Started(new ProcessLanguageServer(process, request, post, _time));
    }
}

/// <summary>One running server: its process, its connection, and how far along it is.</summary>
public sealed class ProcessLanguageServer : ILanguageServerSession, ILspServerMessages
{
    private readonly Process _process;
    private readonly Action<Action> _post;
    private readonly LspConnection _connection;
    private readonly CancellationTokenSource _closing = new();
    private readonly Queue<string> _complaints = new();
    private readonly object _complaintGate = new();
    private readonly Task _complaintsRead;

    private ServerReadiness _readiness = new ServerReadiness.Handshaked();
    private int _disposed;

    internal ProcessLanguageServer(
        Process process, ServerLaunchRequest request, Action<Action> post, TimeProvider time)
    {
        _process = process;
        _post = post;
        Request = request;

        _connection = LspConnection.Start(
            new LspChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream),
            this,
            time);

        _complaintsRead = ReadComplaintsAsync();

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = ReportExitAsync();
    }

    public event Action<ServerReadiness>? ReadinessChanged;

    public event Action<ServerExit>? Exited;

    public event Action<PublishedDiagnostics>? DiagnosticsPublished;

    public ServerLaunchRequest Request { get; }

    /// <summary>What the server said it can do, once the opening exchange has happened.</summary>
    public ServerCapabilities? Capabilities { get; private set; }

    /// <summary>
    /// Runs the opening exchange. A server that counts positions differently from this client is
    /// refused here rather than allowed to answer with offsets nothing can use.
    /// </summary>
    public async Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var rootUri = DocumentUri.OfFile(Request.ProjectRoot);

        // Held open across the send: the request writes its params when it goes out, not now.
        using var options = Parse(Request.Entry.InitializationOptionsJson);
        var response = await _connection
            .Send(
                LspHandshake.Initialize(rootUri, System.Environment.ProcessId, options?.RootElement),
                timeout,
                ct)
            .ConfigureAwait(false);

        switch (response)
        {
            case LspResponse<ServerCapabilities>.Ok(var capabilities):
                if (!capabilities.CountsPositionsAsWeDo)
                    return $"server counts positions as {capabilities.PositionEncoding}, which this client cannot address.";
                Capabilities = capabilities;
                await _connection.Notify(LspHandshake.Initialized(), ct).ConfigureAwait(false);
                Advance(new ServerReadiness.Handshaked());
                return null;

            case LspResponse<ServerCapabilities>.TimedOut(var after):
                return $"no answer to the opening request within {after.TotalSeconds:0}s.";

            case LspResponse<ServerCapabilities>.Failed(_, var message):
                return $"the opening request was refused: {message}";

            case LspResponse<ServerCapabilities>.Disconnected(var reason):
                // A server that died during startup said why on its error stream, and this is the
                // only place that message can still be reached: "the connection closed" describes
                // what we saw, not what went wrong.
                return await Blame($"the server ended during startup: {reason}").ConfigureAwait(false);

            default:
                return await Blame("the server did not complete the opening request.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits briefly for the error stream to finish before quoting it: the connection notices the
    /// pipe close before the reader has necessarily drained the last line, and the last line is the
    /// one worth having.
    /// </summary>
    private async Task<string> Blame(string what)
    {
        await Task.WhenAny(_complaintsRead, Task.Delay(ComplaintGrace)).ConfigureAwait(false);
        if (LastComplaint() is not { } complaint) return what;
        return what.EndsWith('.') ? $"{what} It said: {complaint}" : $"{what}. It said: {complaint}";
    }

    private static readonly TimeSpan ComplaintGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Asks the server something, and reports what the answer says about the server itself. A real
    /// answer promotes it to ready — readiness is reported from an answer rather than from the
    /// handshake because a server can complete the handshake in milliseconds and still be half a
    /// minute from knowing anything. A refusal that means "ask again" is that same half minute
    /// seen from the other side, and reports the server as still working.
    /// </summary>
    public async Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken ct)
    {
        var response = await _connection.Send(request, timeout, ct).ConfigureAwait(false);
        switch (response)
        {
            case LspResponse<T>.Ok:
                Advance(new ServerReadiness.Ready());
                break;
            // Only from the handshake: a percentage the server sent itself says more than this
            // does, and must not be replaced by a refusal that carries no number.
            case LspResponse<T>.Retryable when _readiness is ServerReadiness.Handshaked:
                Advance(new ServerReadiness.Indexing(null));
                break;
        }

        return response;
    }

    /// <summary>Tells the server about a file, at the text on disk. There is no counterpart that
    /// sends an edit: this client only ever reads, so the server's copy cannot fall behind ours.</summary>
    public Task OpenAsync(
        DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken ct) =>
        _connection.Notify(LspNotices.DidOpen(uri, language, version, text), ct);

    public Task CloseAsync(DocumentUri uri, CancellationToken ct) =>
        _connection.Notify(LspNotices.DidClose(uri), ct);

    public void RequestShutdown()
    {
        _ = ShutdownAsync();

        async Task ShutdownAsync()
        {
            try
            {
                await _connection.Send(LspHandshake.Shutdown(), TimeSpan.FromSeconds(5), _closing.Token)
                    .ConfigureAwait(false);
                await _connection.Notify(LspHandshake.Exit(), _closing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // A server that will not be told is killed by the supervisor's grace timer.
            }
            finally
            {
                // The close is what actually ends most servers; the exchange above is the polite form.
                try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    void ILspServerMessages.OnNotification(ServerNotification notification)
    {
        if (notification is ServerNotification.Diagnostics diagnostics)
        {
            var published = new PublishedDiagnostics(
                diagnostics.Uri,
                diagnostics.Version is { } version ? ResultVersion.At(version) : ResultVersion.Untagged,
                diagnostics.Items);
            Raise(() => DiagnosticsPublished?.Invoke(published));
            return;
        }

        if (notification is not ServerNotification.Other(var method, var payload)) return;
        if (method != LspMethod.Progress) return;
        if (ReadProgress(payload) is { } working) Advance(working);
    }

    /// <summary>
    /// The one thing a server asks of this client. We advertise <c>window.workDoneProgress</c>, so a
    /// server may ask to open a progress token, and refusing it is not a polite no: the request is
    /// how the server obeys a capability we claimed, and typescript-language-server treats the
    /// refusal as fatal and exits. Everything else is genuinely not implemented and says so.
    /// </summary>
    Task<InboundReply> ILspServerMessages.OnRequest(ServerRequest request, CancellationToken ct) =>
        Task.FromResult(ReplyTo(request.Method));

    internal static InboundReply ReplyTo(LspMethod method) =>
        method == LspMethod.CreateWorkDoneProgress
            ? new InboundReply.Ok(writer => writer.WriteNullValue())
            : new InboundReply.NotHandled();

    void ILspServerMessages.OnFault(LspFault fault) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closing.Cancel();
        _ = _connection.DisposeAsync();
        Kill();
        _closing.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Readiness only moves forward. Progress reports arrive interleaved and out of order, and a
    /// server that has answered once must not be redrawn as still indexing.
    /// </summary>
    private void Advance(ServerReadiness next)
    {
        if (Rank(next) < Rank(_readiness)) return;
        if (next == _readiness) return;
        _readiness = next;
        Raise(() => ReadinessChanged?.Invoke(next));
    }

    private static int Rank(ServerReadiness readiness) => readiness switch
    {
        ServerReadiness.Handshaked => 0,
        ServerReadiness.Indexing => 1,
        ServerReadiness.Ready => 2,
        _ => 0,
    };

    /// <summary>
    /// The server's own options, straight from the config file. Unreadable text is dropped rather
    /// than failing the launch: it is the one field this client never interprets, so the server is
    /// the only thing that could have judged it anyway.
    /// </summary>
    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A progress report that means work is under way, and the percentage if it carries one — which
    /// most do not. A server saying "Initializing JS/TS language features…" with no number is still
    /// telling us it is alive and busy, and reading only the number threw that away: the server
    /// looked identical to one that had wedged.
    /// </summary>
    internal static ServerReadiness? ReadProgress(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;

        // "end" is the work finishing, not work happening. Readiness past that comes from an answer.
        if (value.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String &&
            kind.GetString() == "end")
            return null;

        return new ServerReadiness.Indexing(ReadPercent(value));
    }

    private static int? ReadPercent(JsonElement value)
    {
        return value.TryGetProperty("percentage", out var percentage) && percentage.TryGetInt32(out var percent)
            ? percent
            : null;
    }

    private int? SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Reports the exit, but not before the error stream has had its moment. The exit event fires
    /// the instant the process is reaped, which is ahead of the last line it wrote reaching us —
    /// and that line is the whole reason anyone reads this message.
    /// </summary>
    private async Task ReportExitAsync()
    {
        var code = SafeExitCode();
        await Task.WhenAny(_complaintsRead, Task.Delay(ComplaintGrace)).ConfigureAwait(false);
        var detail = LastComplaint();
        Raise(() => Exited?.Invoke(new ServerExit(code, detail)));
    }

    private void Raise(Action action) => _post(action);

    private const int ComplaintsKept = 4;

    private const int ComplaintLineCap = 200;

    /// <summary>
    /// Drains the server's error stream and keeps the tail of it. Both halves matter: a server that
    /// died said why on this stream and nowhere else, and a pipe nobody reads fills up and stops a
    /// server that was working — every server measured logs here, some of them constantly.
    /// </summary>
    private async Task ReadComplaintsAsync()
    {
        try
        {
            // Deliberately not cancellable. The stream ends when the process does, and tearing the
            // server down is exactly when its last words matter — a read cancelled by our own
            // shutdown drops the one line that says why it died.
            var reader = _process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Trim() is not { Length: > 0 } trimmed) continue;
                if (trimmed.Length > ComplaintLineCap) trimmed = trimmed[..ComplaintLineCap];

                lock (_complaintGate)
                {
                    _complaints.Enqueue(trimmed);
                    while (_complaints.Count > ComplaintsKept) _complaints.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private string? LastComplaint()
    {
        lock (_complaintGate)
            return _complaints.Count == 0 ? null : string.Join(" / ", _complaints);
    }
}
