using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>
/// The two halves of a server's pipe, named so they cannot be swapped at a call site the way two
/// bare Streams can.
/// </summary>
public sealed record LspChannel(Stream Incoming, Stream Outgoing);

/// <summary>
/// One conversation with one language server. Owns framing, id allocation and response matching, and
/// nothing above that: it knows a byte stream and JSON-RPC, and learns what a result means only from
/// the reader the caller hands it.
/// </summary>
public sealed class LspConnection : IAsyncDisposable
{
    private const int RememberedAbandonedIds = 256;

    private readonly LspChannel _channel;
    private readonly ILspServerMessages _handler;
    private readonly TimeProvider _clock;
    private readonly LspFrameReader _reader;
    private readonly LspFrameWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<RequestId, IPending> _pending = new();
    private readonly ConcurrentDictionary<RequestId, byte> _abandoned = new();
    private readonly ConcurrentQueue<RequestId> _abandonedOrder = new();
    private readonly Task _loop;
    private long _nextId;
    private int _closed;

    private LspConnection(LspChannel channel, ILspServerMessages handler, TimeProvider clock, LspFrameLimits? limits)
    {
        _channel = channel;
        _handler = handler;
        _clock = clock;
        _reader = new LspFrameReader(channel.Incoming, limits);
        _writer = new LspFrameWriter(channel.Outgoing);
        _loop = Task.Run(ReadLoop);
    }

    public static LspConnection Start(
        LspChannel channel,
        ILspServerMessages handler,
        TimeProvider clock,
        LspFrameLimits? limits = null) => new(channel, handler, clock, limits);

    /// <summary>Asks the server a question. Never throws for a protocol or transport outcome.</summary>
    public async Task<LspResponse<T>> Send<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new LspResponse<T>.Cancelled();
        if (Volatile.Read(ref _closed) == 1) return new LspResponse<T>.Disconnected("the connection is closed");

        var id = new RequestId.Number(Interlocked.Increment(ref _nextId));
        var pending = new Pending<T>(request.ReadResult);
        _pending[id] = pending;

        if (timeout != Timeout.InfiniteTimeSpan)
            pending.Attach(_clock.CreateTimer(_ => Abandon(id, p => p.DeliverTimeout(timeout)), null, timeout, Timeout.InfiniteTimeSpan));
        pending.Attach(ct.Register(() => Abandon(id, p => p.DeliverCancelled())));

        try
        {
            await WriteMessage(writer =>
            {
                id.Write(WriteName(writer, "id"));
                writer.WriteString("method", request.Method.Name);
                writer.WritePropertyName("params");
                request.WriteParams(writer);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (_pending.TryRemove(id, out var dropped)) dropped.DeliverDisconnected(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (_pending.TryRemove(id, out var dropped)) dropped.DeliverCancelled();
        }

        return await pending.Task.ConfigureAwait(false);
    }

    /// <summary>Tells the server something. There is no reply to wait for.</summary>
    public async Task Notify(LspNotice notice, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _closed) == 1) return;
        try
        {
            await WriteMessage(writer =>
            {
                writer.WriteString("method", notice.Method.Name);
                writer.WritePropertyName("params");
                notice.WriteParams(writer);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A notification to a server that has gone away is not a failure anyone can act on.
        }
    }

    private static Utf8JsonWriter WriteName(Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        return writer;
    }

    private ValueTask WriteMessage(WriteJson body, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }

        return _writer.WriteAsync(buffer.WrittenMemory, ct);
    }

    private void Abandon(RequestId id, Action<IPending> outcome)
    {
        if (!_pending.TryRemove(id, out var pending)) return;

        Remember(id);
        outcome(pending);

        // The server is still working on it; tell it to stop.
        _ = Notify(new LspNotice(LspMethod.CancelRequest, writer =>
        {
            writer.WriteStartObject();
            id.Write(WriteName(writer, "id"));
            writer.WriteEndObject();
        }));
    }

    private void Remember(RequestId id)
    {
        _abandoned[id] = 0;
        _abandonedOrder.Enqueue(id);
        while (_abandonedOrder.Count > RememberedAbandonedIds && _abandonedOrder.TryDequeue(out var old))
            _abandoned.TryRemove(old, out _);
    }

    private async Task ReadLoop()
    {
        var reason = "the connection was closed";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                switch (read)
                {
                    case FrameRead.Frame frame:
                        Dispatch(frame.Payload);
                        continue;

                    case FrameRead.Closed:
                        reason = "the server closed the connection";
                        Report(new LspFault.ConnectionEnded(reason, Clean: true));
                        break;

                    case FrameRead.Truncated truncated:
                        reason = truncated.Detail;
                        Report(new LspFault.ConnectionEnded(reason, Clean: false));
                        break;

                    case FrameRead.Malformed malformed:
                        reason = malformed.Detail;
                        Report(new LspFault.FramingFailed(malformed.Fault, malformed.Detail));
                        break;

                    default:
                        throw new NotSupportedException($"unhandled frame read {read.GetType().Name}");
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            Report(new LspFault.ConnectionEnded(reason, Clean: false));
        }

        FailEverythingPending(reason);
    }

    private void Dispatch(byte[] payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            Report(new LspFault.UnreadableMessage(ex.Message));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Report(new LspFault.UnreadableMessage($"a message must be an object, was {root.ValueKind}"));
                return;
            }

            try
            {
                if (root.TryGetProperty("method", out var method))
                {
                    var name = new LspMethod(method.AsString("a method"));
                    if (root.TryGetProperty("id", out var requestId))
                        StartServerRequest(new ServerRequest(RequestId.Read(requestId), name, ParamsOf(root)));
                    else
                        DeliverNotification(name, ParamsOf(root));
                    return;
                }

                DeliverResponse(root);
            }
            catch (LspParseException ex)
            {
                Report(new LspFault.UnreadableMessage(ex.Message));
            }
        }
    }

    private static JsonElement ParamsOf(JsonElement root) =>
        root.TryGetProperty("params", out var value) ? value.Clone() : default;

    private void DeliverNotification(LspMethod method, JsonElement parameters)
    {
        ServerNotification notification;
        try
        {
            notification = ServerNotifications.Read(method, parameters);
        }
        catch (LspParseException ex)
        {
            Report(new LspFault.UnreadableMessage($"{method}: {ex.Message}"));
            return;
        }

        try
        {
            _handler.OnNotification(notification);
        }
        catch (Exception ex)
        {
            Report(new LspFault.HandlerFailed(method, ex.Message));
        }
    }

    private void DeliverResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            Report(new LspFault.UnreadableMessage("a message with no method and no id"));
            return;
        }

        var id = RequestId.Read(idElement);
        if (!_pending.TryRemove(id, out var pending))
        {
            // A reply to something we timed out or cancelled is expected and uninteresting. A reply
            // to an id we never issued is a server bug worth surfacing.
            if (!_abandoned.ContainsKey(id)) Report(new LspFault.UnmatchedResponse(id));
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = new LspErrorCode(error.Require("code").AsCodeNumber());
            var message = error.Optional("message")?.AsString("an error message") ?? string.Empty;
            pending.DeliverError(code, message);
            return;
        }

        pending.DeliverResult(root.TryGetProperty("result", out var result) ? result : default);
    }

    private void StartServerRequest(ServerRequest request)
    {
        // On its own task: a handler that takes its time must not stop responses arriving.
        _ = Task.Run(async () =>
        {
            InboundReply reply;
            try
            {
                reply = await _handler.OnRequest(request, _stop.Token).ConfigureAwait(false)
                        ?? new InboundReply.NotHandled();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report(new LspFault.HandlerFailed(request.Method, ex.Message));
                reply = new InboundReply.Error(LspErrorCode.InternalError, ex.Message);
            }

            await Answer(request.Id, reply).ConfigureAwait(false);
        });
    }

    private async Task Answer(RequestId id, InboundReply reply)
    {
        try
        {
            await WriteMessage(writer =>
            {
                id.Write(WriteName(writer, "id"));
                switch (reply)
                {
                    case InboundReply.Ok ok:
                        writer.WritePropertyName("result");
                        ok.WriteResult(writer);
                        break;
                    case InboundReply.Error error:
                        WriteError(writer, error.Code, error.Message);
                        break;
                    case InboundReply.NotHandled:
                        WriteError(writer, LspErrorCode.MethodNotFound, "this client does not implement that method");
                        break;
                    default:
                        throw new NotSupportedException($"unhandled reply {reply.GetType().Name}");
                }
            }, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The server is gone; there is nobody to answer.
        }

        static void WriteError(Utf8JsonWriter writer, LspErrorCode code, string message)
        {
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code.Value);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }
    }

    private void Report(LspFault fault)
    {
        try
        {
            _handler.OnFault(fault);
        }
        catch
        {
            // A reporter that throws must not take the read loop with it.
        }
    }

    private void FailEverythingPending(string reason)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending))
                pending.DeliverDisconnected(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;

        await _stop.CancelAsync().ConfigureAwait(false);
        _channel.Outgoing.Dispose();
        _channel.Incoming.Dispose();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // The loop's own faults were already reported.
        }

        FailEverythingPending("the connection was closed");
        _writer.Dispose();
        _stop.Dispose();
    }

    private interface IPending
    {
        void DeliverResult(JsonElement result);

        void DeliverError(LspErrorCode code, string message);

        void DeliverTimeout(TimeSpan after);

        void DeliverCancelled();

        void DeliverDisconnected(string reason);
    }

    private sealed class Pending<T>(ILspResultReader<T> reader) : IPending
    {
        private readonly TaskCompletionSource<LspResponse<T>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<IDisposable> _attached = [];

        public Task<LspResponse<T>> Task => _completion.Task;

        public void Attach(IDisposable resource)
        {
            lock (_attached) _attached.Add(resource);
            if (_completion.Task.IsCompleted) Release();
        }

        public void DeliverResult(JsonElement result)
        {
            LspResponse<T> response;
            try
            {
                response = new LspResponse<T>.Ok(reader.Read(result));
            }
            catch (LspParseException ex)
            {
                response = new LspResponse<T>.Malformed(ex.Message);
            }

            Finish(response);
        }

        public void DeliverError(LspErrorCode code, string message)
        {
            if (code.MeansCancelled) Finish(new LspResponse<T>.Cancelled());
            else if (code.MeansAskAgain) Finish(new LspResponse<T>.Retryable(code, message));
            else Finish(new LspResponse<T>.Failed(code, message));
        }

        public void DeliverTimeout(TimeSpan after) => Finish(new LspResponse<T>.TimedOut(after));

        public void DeliverCancelled() => Finish(new LspResponse<T>.Cancelled());

        public void DeliverDisconnected(string reason) => Finish(new LspResponse<T>.Disconnected(reason));

        private void Finish(LspResponse<T> response)
        {
            _completion.TrySetResult(response);
            Release();
        }

        private void Release()
        {
            IDisposable[] resources;
            lock (_attached)
            {
                resources = _attached.ToArray();
                _attached.Clear();
            }

            foreach (var resource in resources) resource.Dispose();
        }
    }
}

internal static class ErrorCodeJson
{
    public static int AsCodeNumber(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var code)
            ? code
            : throw new LspParseException($"an error code must be a number, was {element.ValueKind}");
}
