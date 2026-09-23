using System.Threading.Channels;
using GitBench.Features.Editor;
using GitBench.Features.AgentConnections;
using GitBench.Features.AgentConnections.Acp;
using GitBench.Git;
using GitBench.Lsp.Lifecycle;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// Runs a conversation's agent over ACP: starts the adapter against the app's MCP server, sends the
/// opening turn, and streams what the agent says into the panel. While a pairing session is live a
/// turn that ends gets a nudge to carry on, and an agent that stops calling tools is reported gone
/// from the session; between sessions each turn is what the user says next. The write guard lives
/// in the connection; what it refused is shown, and what it has no rule for is asked of the user in
/// the panel.
/// </summary>
internal sealed class AcpPairingDriver : IAcpPermissionPrompt, IAgentDriver
{
    /// <summary>Turns in a row without a single tool call before the agent counts as gone.</summary>
    private const int IdleTurnLimit = 3;

    private const string ServerName = "diffdino";

    private readonly AgentConversation _conversation;
    private readonly AgentPrompt _opening;
    private readonly AcpHarness _harness;
    private readonly AgentEndpoints _endpoints;
    private readonly IServerEnvironment _environment;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _toolCalls = new();
    private readonly Channel<AgentPrompt> _inbox = Channel.CreateUnbounded<AgentPrompt>(new UnboundedChannelOptions { SingleReader = true });
    private AcpAgentConnection? _connection;
    private Task _run = Task.CompletedTask;
    private int _refusedThisTurn;

    private AcpPairingDriver(
        AgentConversation conversation, AgentPrompt opening, AcpHarness harness, AgentEndpoints endpoints, IServerEnvironment environment,
        IUiDispatcher dispatcher)
    {
        _conversation = conversation;
        _opening = opening;
        _harness = harness;
        _endpoints = endpoints;
        _environment = environment;
        _dispatcher = dispatcher;
    }

    /// <summary>Starts the agent for a conversation, with <paramref name="opening"/> as its first
    /// turn. UI thread.</summary>
    public static AcpPairingDriver Start(
        AgentConversation conversation, AgentPrompt opening, AcpHarness harness, AgentEndpoints endpoints, IServerEnvironment environment,
        IUiDispatcher dispatcher)
    {
        var driver = new AcpPairingDriver(conversation, opening, harness, endpoints, environment, dispatcher);
        driver._run = driver.RunAsync();
        return driver;
    }

    public void Tell(AgentPrompt prompt) => _inbox.Writer.TryWrite(prompt);

    // A quote goes as the file's text attached to the turn where the agent takes that, and as
    // markdown in the prose where it doesn't.
    private IReadOnlyList<AcpContent> Content(AcpAgentConnection connection, AgentPrompt prompt)
    {
        var repoPath = _conversation.Repo.Path;
        // Only a file's lines can be attached: a diff's may be ones the change removed.
        if (prompt.Quote is not CodeQuote.InFile file || !connection.EmbedsContext) return [new AcpContent.Prose(prompt.ToMarkdown(repoPath))];
        var path = AgentPrompt.RepoRelative(repoPath, file.AbsolutePath);
        return
        [
            new AcpContent.Prose(prompt.Text + $"\n\n(Attached: what I selected in `{path}`, {file.Lines}.)"),
            new AcpContent.Resource(file.Uri, file.Text),
        ];
    }

    private async Task RunAsync()
    {
        try
        {
            Uri url;
            switch (await OnUi(() => _endpoints.EnsureAsync(_stop.Token)).ConfigureAwait(false))
            {
                case AgentEndpoint.Listening listening:
                    url = listening.Url;
                    break;
                case AgentEndpoint.Unavailable unavailable:
                    Post(() => _conversation.Fail($"DiffDino's agent connections server is not available: {unavailable.Reason}"));
                    return;
                default:
                    throw new InvalidOperationException("Unhandled endpoint.");
            }

            AcpAgentConnection connection;
            switch (await AcpAgentConnection.StartAsync(_harness, _conversation.Repo.Path, new AcpMcpServer(ServerName, url), this, _environment, _stop.Token)
                        .ConfigureAwait(false))
            {
                case AcpStart.Started started:
                    connection = started.Connection;
                    break;
                case AcpStart.Failed failed:
                    Post(() => _conversation.Fail($"{_harness.Label} could not be started. {failed.Reason}"));
                    return;
                default:
                    throw new InvalidOperationException("Unhandled start outcome.");
            }

            _connection = connection;
            Trace(connection);
            connection.Updated += OnUpdate;
            connection.PermissionDecided += OnPermissionDecided;
            _ = connection.Closed.ContinueWith(_ => OnClosed(connection), TaskScheduler.Default);
            Post(_conversation.MarkRunning);

            await Converse(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (AcpClosedException)
        {
            // OnClosed reports it.
        }
        catch (AcpRpcException e)
        {
            Post(() => _conversation.Fail($"{_harness.Label} failed: {e.Message}"));
        }
    }

    private async Task Converse(AcpAgentConnection connection)
    {
        var prompt = _opening;
        var idle = 0;
        while (!_stop.IsCancellationRequested)
        {
            var before = ToolCallCount;
            Interlocked.Exchange(ref _refusedThisTurn, 0);
            Post(_conversation.BeginTurn);
            var reason = await connection.PromptAsync(Content(connection, prompt), _stop.Token).ConfigureAwait(false);
            Post(_conversation.EndTurn);
            if (_stop.IsCancellationRequested) return;

            var pairing = await OnUi(() => Task.FromResult(_conversation.IsPairing)).ConfigureAwait(false);
            if (reason == AcpStopReason.Refusal)
            {
                var refused = $"{_harness.Label} refused to continue.";
                if (pairing) Post(() => _conversation.Session.Value?.Store.MarkDisconnected(refused));
                else Post(() => _conversation.Transcript.AddNotice(refused, NoticeTone.Error));
                pairing = false;
            }

            // What the user said, or a session they started, while the turn ran comes first.
            if (_inbox.Reader.TryRead(out var queued))
            {
                idle = 0;
                prompt = queued;
                continue;
            }

            if (pairing)
            {
                idle = ToolCallCount == before ? idle + 1 : 0;
                if (idle < IdleTurnLimit)
                {
                    prompt = new AgentPrompt(Volatile.Read(ref _refusedThisTurn) > 0
                        ? "Writing files is refused: the user writes the code. " + PairingInstructions.Continue
                        : PairingInstructions.Continue);
                    continue;
                }

                Post(() => _conversation.Session.Value?.Store.MarkDisconnected($"{_harness.Label} stopped calling the pairing tools."));
            }

            idle = 0;
            prompt = await _inbox.Reader.ReadAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    // DIFFDINO_ACP_TRACE names a directory to write each session's protocol traffic into, one file
    // per session: the only way to see what an agent actually sent when the panel shows otherwise.
    private static void Trace(AcpAgentConnection connection)
    {
        if (Environment.GetEnvironmentVariable("DIFFDINO_ACP_TRACE") is not { Length: > 0 } directory) return;
        try
        {
            Directory.CreateDirectory(directory);
            var writer = new StreamWriter(Path.Combine(directory, $"acp-{DateTime.Now:yyyyMMdd-HHmmss}.log")) { AutoFlush = true };
            var gate = new object();
            connection.Traffic += traffic =>
            {
                lock (gate) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(traffic.Direction == AcpDirection.Incoming ? "<<" : ">>")} {traffic.Line}");
            };
            _ = connection.Closed.ContinueWith(_ =>
            {
                lock (gate) writer.Dispose();
            }, TaskScheduler.Default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private int ToolCallCount
    {
        get
        {
            lock (_toolCalls) return _toolCalls.Count;
        }
    }

    private void OnUpdate(AcpSessionUpdate update)
    {
        switch (update)
        {
            case AcpSessionUpdate.MessageChunk chunk:
                if (chunk.Text.Length > 0) Post(() => _conversation.Transcript.AppendNarration(chunk.Text));
                break;
            case AcpSessionUpdate.ToolCall call:
                lock (_toolCalls) _toolCalls.Add(call.Id);
                break;
            case AcpSessionUpdate.ThoughtChunk:
            case AcpSessionUpdate.Plan:
            case AcpSessionUpdate.Other:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(update), update, "Unknown update.");
        }
    }

    private void OnPermissionDecided(AcpPermissionRequest request, AcpPermissionVerdict verdict)
    {
        if (verdict != AcpPermissionVerdict.Rejected) return;
        Interlocked.Increment(ref _refusedThisTurn);
        var what = request.Title.Length > 0 ? request.Title : request.Kind.ToString();
        Post(() => _conversation.Transcript.AddNotice($"Refused {Describe(request.Kind)}: {what}", NoticeTone.Refused));
    }

    private static string Describe(AcpToolKind kind) => kind switch
    {
        AcpToolKind.Edit => "an edit",
        AcpToolKind.Delete => "a delete",
        AcpToolKind.Move => "a move",
        AcpToolKind.Execute => "a command",
        _ => "a tool call",
    };

    async Task<string?> IAcpPermissionPrompt.AskAsync(AcpPermissionRequest request, CancellationToken ct)
    {
        var pending = await OnUi(() => Task.FromResult(_conversation.Transcript.AskPermission(request.Title, request.Command ?? request.Kind.ToString())))
            .ConfigureAwait(false);
        bool approved;
        try
        {
            approved = await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Post(pending.Cancel);
            return null;
        }

        foreach (var wanted in approved
                     ? new[] { AcpPermissionOptionKind.AllowOnce, AcpPermissionOptionKind.AllowAlways }
                     : [AcpPermissionOptionKind.RejectOnce, AcpPermissionOptionKind.RejectAlways])
            foreach (var option in request.Options)
                if (option.Kind == wanted)
                    return option.OptionId;
        return null;
    }

    private void OnClosed(AcpAgentConnection connection)
    {
        if (_stop.IsCancellationRequested) return;
        var tail = connection.StderrTail;
        Post(() => _conversation.Fail(tail.Length == 0 ? $"{_harness.Label} exited." : $"{_harness.Label} exited.\n{tail}"));
    }

    private void Post(Action action) => _dispatcher.Post(action);

    private async Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        });
        var inner = await completion.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
        return await inner.ConfigureAwait(false);
    }

    /// <summary>Stops the agent. UI thread.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _inbox.Writer.TryComplete();
        if (_connection is { } connection)
        {
            await connection.CancelTurnAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
