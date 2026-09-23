using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitBench.Features.AgentConnections.Acp;

/// <summary>What the agent sent the client unasked: a notification, or a request the client must
/// answer.</summary>
internal interface IAcpClientMessages
{
    void OnNotification(string method, JsonNode? parameters);

    /// <summary>Answers a request from the agent. Throwing <see cref="AcpRpcException"/> answers
    /// with that error; the task may complete on any thread.</summary>
    Task<JsonNode?> OnRequest(string method, JsonNode? parameters, CancellationToken ct);
}

/// <summary>An error the other side answered a request with, or one this side answers with.</summary>
internal sealed class AcpRpcException : Exception
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public AcpRpcException(int code, string message) : base(message) => Code = code;

    public int Code { get; }
}

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON, the framing the Agent Client Protocol uses on an
/// agent's stdio. Owns id allocation, response matching and the read loop; it knows nothing of
/// what the methods mean. A line that is not a JSON object is skipped: adapters log to stdout.
/// </summary>
internal sealed class AcpJsonRpc : IAsyncDisposable
{
    private readonly TextReader _incoming;
    private readonly TextWriter _outgoing;
    private readonly IAcpClientMessages _handler;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private Task? _loop;

    public AcpJsonRpc(TextReader incoming, TextWriter outgoing, IAcpClientMessages handler)
    {
        _incoming = incoming;
        _outgoing = outgoing;
        _handler = handler;
    }

    /// <summary>Completes when the incoming stream ends or the connection is disposed.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Every line in either direction, for a trace. Raised on the thread that moved it.</summary>
    public event Action<AcpTraffic>? Traffic;

    public void Start() => _loop ??= Task.Run(ReadLoop);

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (_closed.Task.IsCompleted)
        {
            _pending.TryRemove(id, out _);
            throw new AcpClosedException();
        }

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(ct);
        });
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    public Task NotifyAsync(string method, JsonNode? parameters) => WriteAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters,
    });

    private async Task ReadLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested
                   && await _incoming.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                Traffic?.Invoke(new AcpTraffic(AcpDirection.Incoming, line));
                if (Parse(line) is not { } message) continue;
                try
                {
                    Dispatch(message);
                }
                catch (InvalidOperationException)
                {
                    // A message whose fields are not the shapes the protocol says: skipped, like a
                    // line that isn't JSON at all.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Close();
        }
    }

    private static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Dispatch(JsonObject message)
    {
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        var id = message["id"];

        if (method is null)
        {
            if (id is JsonValue value && value.TryGetValue<long>(out var number)
                && _pending.TryRemove(number, out var pending))
            {
                if (message["error"] is JsonObject error)
                    pending.TrySetException(new AcpRpcException(
                        error["code"] is JsonValue c && c.TryGetValue<int>(out var code) ? code : AcpRpcException.InternalError,
                        error["message"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "The agent answered with an error."));
                else
                    pending.TrySetResult(message["result"]?.DeepClone());
            }

            return;
        }

        var parameters = message["params"]?.DeepClone();
        if (id is null)
        {
            _handler.OnNotification(method, parameters);
            return;
        }

        _ = AnswerAsync(id.DeepClone(), method, parameters);
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        JsonObject reply;
        try
        {
            var result = await _handler.OnRequest(method, parameters, _stop.Token).ConfigureAwait(false);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (AcpRpcException e)
        {
            reply = ErrorReply(id, e.Code, e.Message);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await WriteAsync(reply).ConfigureAwait(false);
        }
        catch (AcpClosedException)
        {
        }
    }

    private static JsonObject ErrorReply(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task WriteAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed.Task.IsCompleted) throw new AcpClosedException();
            await _outgoing.WriteAsync(line + "\n").ConfigureAwait(false);
            await _outgoing.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            Close();
            throw new AcpClosedException();
        }
        catch (ObjectDisposedException)
        {
            Close();
            throw new AcpClosedException();
        }
        finally
        {
            _writeLock.Release();
        }

        Traffic?.Invoke(new AcpTraffic(AcpDirection.Outgoing, line));
    }

    private void Close()
    {
        if (!_closed.TrySetResult()) return;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetException(new AcpClosedException());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Close();
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}

/// <summary>The agent's side of the pipe is gone.</summary>
internal sealed class AcpClosedException : Exception
{
    public AcpClosedException() : base("The agent's connection is closed.") { }
}

internal enum AcpDirection
{
    Incoming,
    Outgoing,
}

internal readonly record struct AcpTraffic(AcpDirection Direction, string Line);
