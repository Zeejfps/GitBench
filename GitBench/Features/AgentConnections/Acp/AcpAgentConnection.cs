using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.AgentConnections.Acp;

/// <summary>Asks the user about a permission request the policy leaves open. Answers the chosen
/// option id, or null to cancel the tool call.</summary>
internal interface IAcpPermissionPrompt
{
    Task<string?> AskAsync(AcpPermissionRequest request, CancellationToken ct);
}

/// <summary>The app's MCP server as a session is handed it.</summary>
internal sealed record AcpMcpServer(string Name, Uri Url);

/// <summary>How starting an agent went.</summary>
internal abstract record AcpStart
{
    public sealed record Started(AcpAgentConnection Connection) : AcpStart;

    public sealed record Failed(string Reason) : AcpStart;
}

/// <summary>
/// One agent run over the Agent Client Protocol: the adapter process, its one session, and the
/// write guard. Every permission request goes through <see cref="AcpPermissionPolicy"/> — reads,
/// shell commands and the app's own MCP tools pass, file edits are refused, the rest is asked — and the
/// session is put in its harness's asking mode first, so that no write is decided inside the CLI.
/// Events are raised on the reader's thread.
/// </summary>
internal sealed class AcpAgentConnection : IAsyncDisposable, IAcpClientMessages
{
    /// <summary>Whether the agent takes a file's text attached to a turn as context, rather than
    /// only prose.</summary>
    public bool EmbedsContext { get; private set; }

    private const int StderrLines = 40;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(90);

    private readonly AcpJsonRpc _rpc;
    private readonly Process? _process;
    private readonly IAcpPermissionPrompt _prompt;
    private readonly string _ownServer;
    private readonly Dictionary<string, string> _announcedServers = new();
    private readonly Queue<string> _stderr = new();
    private string? _sessionId;
    private int _disposed;

    private AcpAgentConnection(TextReader incoming, TextWriter outgoing, Process? process, IAcpPermissionPrompt prompt, string ownServer)
    {
        _process = process;
        _prompt = prompt;
        _ownServer = ownServer;
        _rpc = new AcpJsonRpc(incoming, outgoing, this);
    }

    /// <summary>A fragment of the agent's output, a tool call, or its plan.</summary>
    public event Action<AcpSessionUpdate>? Updated;

    /// <summary>A permission request and what was answered, including the ones the user decided.</summary>
    public event Action<AcpPermissionRequest, AcpPermissionVerdict>? PermissionDecided;

    /// <summary>Every protocol line in either direction, for a trace. Raised on the thread that moved it.</summary>
    public event Action<AcpTraffic>? Traffic
    {
        add => _rpc.Traffic += value;
        remove => _rpc.Traffic -= value;
    }

    /// <summary>Completes when the agent's output ends: it exited, crashed, or was disposed.</summary>
    public Task Closed => _rpc.Closed;

    /// <summary>The tail of what the adapter wrote to stderr, for a failure message.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderr) return string.Join('\n', _stderr);
        }
    }

    public static async Task<AcpStart> StartAsync(
        AcpHarness harness,
        string workingDirectory,
        AcpMcpServer server,
        IAcpPermissionPrompt prompt,
        IServerEnvironment environment,
        CancellationToken ct)
    {
        if (environment.ResolveCommand(harness.Command) is not { } executable)
            return new AcpStart.Failed($"'{harness.Command}' was not found on PATH.");

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in harness.Args) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment.Variables) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new AcpStart.Failed($"'{harness.Command}' could not be started: {e.Message}");
        }

        var connection = new AcpAgentConnection(process.StandardOutput, process.StandardInput, process, prompt, server.Name);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) connection.NoteStderr(line);
        };
        process.BeginErrorReadLine();

        string? failure;
        try
        {
            failure = await connection.OpenAsync(workingDirectory, server, harness.AskingMode, harness.SessionMetaJson, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (failure is null) return new AcpStart.Started(connection);

        await connection.DisposeAsync().ConfigureAwait(false);
        var tail = connection.StderrTail;
        return new AcpStart.Failed(tail.Length == 0 ? failure : $"{failure}\n{tail}");
    }

    /// <summary>A connection over given streams, for tests: no process to own.</summary>
    internal static AcpAgentConnection Over(TextReader incoming, TextWriter outgoing, IAcpPermissionPrompt prompt, string ownServer) =>
        new(incoming, outgoing, null, prompt, ownServer);

    /// <summary>Handshakes, opens the session, and puts it in <paramref name="askingMode"/>.
    /// Answers why it could not, or null.</summary>
    internal async Task<string?> OpenAsync(
        string workingDirectory, AcpMcpServer server, string askingMode, string? sessionMetaJson, CancellationToken ct)
    {
        _rpc.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            var initialized = await _rpc.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new JsonObject { ["name"] = "DiffDino", ["version"] = "1" },
            }, timeout.Token).ConfigureAwait(false);
            EmbedsContext = initialized?["agentCapabilities"]?["promptCapabilities"]?["embeddedContext"] is JsonValue embeds
                            && embeds.TryGetValue<bool>(out var embedded) && embedded;

            var session = await _rpc.RequestAsync("session/new", new JsonObject
            {
                ["_meta"] = sessionMetaJson is null ? null : JsonNode.Parse(sessionMetaJson),
                ["cwd"] = workingDirectory,
                ["mcpServers"] = new JsonArray(new JsonObject
                {
                    ["type"] = "http",
                    ["name"] = server.Name,
                    ["url"] = server.Url.ToString(),
                    ["headers"] = new JsonArray(),
                }),
            }, timeout.Token).ConfigureAwait(false);

            if (session?["sessionId"] is not JsonValue id || !id.TryGetValue<string>(out var sessionId))
                return "The agent opened no session.";
            _sessionId = sessionId;

            if (!OffersMode(session, askingMode))
                return $"The agent offers no '{askingMode}' mode, so its writes can't be refused.";
            await _rpc.RequestAsync("session/set_mode", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["modeId"] = askingMode,
            }, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (AcpRpcException e)
        {
            return $"The agent refused to start: {e.Message}";
        }
        catch (AcpClosedException)
        {
            return "The agent exited while starting.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The agent did not answer in time.";
        }
    }

    private static bool OffersMode(JsonNode session, string mode)
    {
        if (session["modes"]?["availableModes"] is not JsonArray modes) return false;
        foreach (var offered in modes)
            if (offered?["id"] is JsonValue id && id.TryGetValue<string>(out var value) && value == mode)
                return true;
        return false;
    }

    /// <summary>Sends one user turn and completes when the agent ends it.</summary>
    public Task<AcpStopReason> PromptAsync(string text, CancellationToken ct) => PromptAsync([new AcpContent.Prose(text)], ct);

    /// <summary>Sends one user turn made of <paramref name="content"/> and completes when the agent
    /// ends it. Attach a <see cref="AcpContent.Resource"/> only where <see cref="EmbedsContext"/>.</summary>
    public async Task<AcpStopReason> PromptAsync(IReadOnlyList<AcpContent> content, CancellationToken ct)
    {
        var sessionId = _sessionId ?? throw new InvalidOperationException("The session is not open.");
        using var cancel = ct.Register(() => _ = CancelTurnAsync());
        var result = await _rpc.RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray([.. content.Select(ToJson)]),
        }, CancellationToken.None).ConfigureAwait(false);

        return (result?["stopReason"] as JsonValue)?.TryGetValue<string>(out var reason) == true
            ? reason switch
            {
                "max_tokens" => AcpStopReason.MaxTokens,
                "max_turn_requests" => AcpStopReason.MaxTurnRequests,
                "refusal" => AcpStopReason.Refusal,
                "cancelled" => AcpStopReason.Cancelled,
                _ => AcpStopReason.EndTurn,
            }
            : AcpStopReason.EndTurn;
    }

    private static JsonNode ToJson(AcpContent content) => content switch
    {
        AcpContent.Prose text => new JsonObject { ["type"] = "text", ["text"] = text.Value },
        AcpContent.Resource resource => new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = resource.Uri.ToString(),
                ["mimeType"] = "text/plain",
                ["text"] = resource.Text,
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown content."),
    };

    /// <summary>Asks the agent to stop the turn in progress; the turn then ends <c>cancelled</c>.</summary>
    public async Task CancelTurnAsync()
    {
        if (_sessionId is not { } sessionId) return;
        try
        {
            await _rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId }).ConfigureAwait(false);
        }
        catch (AcpClosedException)
        {
        }
    }

    void IAcpClientMessages.OnNotification(string method, JsonNode? parameters)
    {
        if (method != "session/update") return;
        var update = parameters?["update"];
        if (update?["sessionUpdate"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "tool_call"
            && update["toolCallId"] is JsonValue idValue && idValue.TryGetValue<string>(out var toolCallId)
            && AcpPermissionPolicy.AnnouncedServer(update) is { } announced)
        {
            lock (_announcedServers) _announcedServers[toolCallId] = announced;
        }

        if (AcpSessionUpdate.Parse(update) is { } parsed) Updated?.Invoke(parsed);
    }

    async Task<JsonNode?> IAcpClientMessages.OnRequest(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (method != "session/request_permission")
            throw new AcpRpcException(AcpRpcException.MethodNotFound, $"DiffDino does not offer {method}.");

        AcpPermissionRequest? request;
        lock (_announcedServers) request = AcpPermissionPolicy.Parse(parameters, _announcedServers);
        if (request is null) throw new AcpRpcException(AcpRpcException.InvalidParams, "Unreadable permission request.");

        string? chosen;
        switch (AcpPermissionPolicy.Decide(request, _ownServer))
        {
            case AcpPermissionDecision.Select select:
                PermissionDecided?.Invoke(request, select.Verdict);
                chosen = select.OptionId;
                break;
            case AcpPermissionDecision.AskUser:
                chosen = await _prompt.AskAsync(request, ct).ConfigureAwait(false);
                PermissionDecided?.Invoke(request, IsAllow(request, chosen) ? AcpPermissionVerdict.Allowed : AcpPermissionVerdict.Rejected);
                break;
            case AcpPermissionDecision.Cancel:
                PermissionDecided?.Invoke(request, AcpPermissionVerdict.Rejected);
                chosen = null;
                break;
            default:
                throw new InvalidOperationException("Unhandled permission decision.");
        }

        return new JsonObject
        {
            ["outcome"] = chosen is null
                ? new JsonObject { ["outcome"] = "cancelled" }
                : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen },
        };
    }

    private static bool IsAllow(AcpPermissionRequest request, string? optionId)
    {
        foreach (var option in request.Options)
            if (option.OptionId == optionId)
                return option.Kind is AcpPermissionOptionKind.AllowOnce or AcpPermissionOptionKind.AllowAlways;
        return false;
    }

    private void NoteStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > StderrLines) _stderr.Dequeue();
        }
    }

    // The process goes first: a read of its output doesn't answer cancellation on every platform,
    // and it is the process ending that lets the read loop finish.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        await _rpc.DisposeAsync().ConfigureAwait(false);
        _process?.Dispose();
    }
}
