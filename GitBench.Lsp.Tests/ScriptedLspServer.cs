using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GitBench.Lsp.Tests;

/// <summary>What the client sent, as the far end of the pipe sees it.</summary>
internal abstract record ClientMessage
{
    public sealed record Request(RequestId Id, string Method, JsonElement Params) : ClientMessage;

    public sealed record Notification(string Method, JsonElement Params) : ClientMessage;

    public sealed record Response(RequestId Id, JsonElement? Result, int? ErrorCode) : ClientMessage;

    public sealed record Junk(string Detail) : ClientMessage;
}

/// <summary>
/// A language server that does exactly what a test tells it to, including the things a real one does
/// wrong: a byte count that lies, a reply to a request nobody made, an exit mid-message. Everything is
/// await-driven, so no test needs to wait for a duration.
/// </summary>
internal sealed class ScriptedLspServer : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly PipeStream _toServer = new();
    private readonly PipeStream _toClient = new();
    private readonly LspFrameReader _reader;
    private readonly LspFrameWriter _writer;

    public ScriptedLspServer()
    {
        _reader = new LspFrameReader(_toServer);
        _writer = new LspFrameWriter(_toClient);
        ClientChannel = new LspChannel(Incoming: _toClient, Outgoing: _toServer);
    }

    public LspChannel ClientChannel { get; }

    /// <summary>The next thing the client sent. Fails the test rather than hanging it.</summary>
    public async Task<ClientMessage> NextMessage()
    {
        var read = await _reader.ReadAsync().AsTask().WaitAsync(Deadline).ConfigureAwait(false);
        return read switch
        {
            FrameRead.Frame frame => Parse(frame.Payload),
            FrameRead.Closed => new ClientMessage.Junk("the client closed the connection"),
            FrameRead.Truncated t => new ClientMessage.Junk(t.Detail),
            FrameRead.Malformed m => new ClientMessage.Junk($"{m.Fault}: {m.Detail}"),
            _ => throw new NotSupportedException(read.GetType().Name),
        };
    }

    public async Task<ClientMessage.Request> NextRequest() =>
        Assert2.Is<ClientMessage.Request>(await NextMessage().ConfigureAwait(false));

    public async Task<ClientMessage.Notification> NextNotification() =>
        Assert2.Is<ClientMessage.Notification>(await NextMessage().ConfigureAwait(false));

    public async Task<ClientMessage.Response> NextResponse() =>
        Assert2.Is<ClientMessage.Response>(await NextMessage().ConfigureAwait(false));

    public async Task<IReadOnlyList<ClientMessage.Request>> TakeRequests(int count)
    {
        var seen = new List<ClientMessage.Request>(count);
        while (seen.Count < count) seen.Add(await NextRequest().ConfigureAwait(false));
        return seen;
    }

    /// <summary>Answers everything it was given, last asked first.</summary>
    public async Task ReplyInReverse(IReadOnlyList<ClientMessage.Request> requests, Func<ClientMessage.Request, string> result)
    {
        foreach (var request in requests.Reverse())
            await ReplyOk(request.Id, result(request)).ConfigureAwait(false);
    }

    /// <summary>The same notification, over and over, as a busy server sends diagnostics.</summary>
    public async Task NotifyMany(LspMethod method, Func<int, string> paramsJson, int count)
    {
        for (var i = 0; i < count; i++) await Notify(method, paramsJson(i)).ConfigureAwait(false);
    }

    public Task ReplyOk(RequestId id, string resultJson) =>
        Send($$"""{"jsonrpc":"2.0","id":{{IdJson(id)}},"result":{{resultJson}}}""");

    public Task ReplyError(RequestId id, LspErrorCode code, string message) =>
        Send("{\"jsonrpc\":\"2.0\",\"id\":" + IdJson(id) +
             ",\"error\":{\"code\":" + code.Value + ",\"message\":" + Quote(message) + "}}");

    public Task Notify(LspMethod method, string paramsJson) =>
        Send($$"""{"jsonrpc":"2.0","method":{{Quote(method.Name)}},"params":{{paramsJson}}}""");

    /// <summary>The server asking the client a question.</summary>
    public Task Ask(RequestId id, LspMethod method, string paramsJson) =>
        Send($$"""{"jsonrpc":"2.0","id":{{IdJson(id)}},"method":{{Quote(method.Name)}},"params":{{paramsJson}}}""");

    /// <summary>One framed message, exactly as written.</summary>
    public async Task Send(string payload)
    {
        try
        {
            await _writer.WriteAsync(Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client hung up. A real server would get EPIPE and carry on dying.
        }
    }

    /// <summary>Bytes straight onto the wire, framing and all. For servers that get it wrong.</summary>
    public async Task SendRaw(string raw)
    {
        try
        {
            await _toClient.WriteAsync(Encoding.UTF8.GetBytes(raw)).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The server process exits.</summary>
    public void Exit() => _toClient.CloseWrite();

    public ValueTask DisposeAsync()
    {
        _toClient.CloseWrite();
        _toServer.CloseWrite();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }

    public static string IdJson(RequestId id) => id switch
    {
        RequestId.Number n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RequestId.Text t => Quote(t.Value),
        _ => throw new NotSupportedException(id.GetType().Name),
    };

    private static string Quote(string value) => $"\"{JsonEncodedText.Encode(value)}\"";

    private static ClientMessage Parse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

        if (root.TryGetProperty("method", out var method))
        {
            var name = method.GetString()!;
            return root.TryGetProperty("id", out var id)
                ? new ClientMessage.Request(RequestId.Read(id), name, parameters)
                : new ClientMessage.Notification(name, parameters);
        }

        if (!root.TryGetProperty("id", out var responseId)) return new ClientMessage.Junk(root.GetRawText());

        return new ClientMessage.Response(
            RequestId.Read(responseId),
            root.TryGetProperty("result", out var result) ? result.Clone() : null,
            root.TryGetProperty("error", out var error) ? error.GetProperty("code").GetInt32() : null);
    }
}

/// <summary>
/// The client's side of the conversation, recorded. Notifications are kept in arrival order; server
/// requests are answered by whatever the test installs.
/// </summary>
internal sealed class RecordingClient : ILspServerMessages
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly Channel<ServerNotification> _notifications = Channel.CreateUnbounded<ServerNotification>();
    private readonly Channel<LspFault> _faults = Channel.CreateUnbounded<LspFault>();
    private readonly Channel<ServerRequest> _requests = Channel.CreateUnbounded<ServerRequest>();

    /// <summary>How the client answers the server. Not handled unless a test says otherwise.</summary>
    public Func<ServerRequest, CancellationToken, Task<InboundReply>> Answer { get; set; } =
        (_, _) => Task.FromResult<InboundReply>(new InboundReply.NotHandled());

    public void OnNotification(ServerNotification notification) => _notifications.Writer.TryWrite(notification);

    public Task<InboundReply> OnRequest(ServerRequest request, CancellationToken ct)
    {
        _requests.Writer.TryWrite(request);
        return Answer(request, ct);
    }

    public void OnFault(LspFault fault) => _faults.Writer.TryWrite(fault);

    public Task<ServerNotification> NextNotification() => Next(_notifications, "notification");

    public Task<LspFault> NextFault() => Next(_faults, "fault");

    public Task<ServerRequest> NextServerRequest() => Next(_requests, "server request");

    public bool AnyFaultSeen => _faults.Reader.Count > 0;

    public async Task<IReadOnlyList<ServerNotification>> TakeNotifications(int count)
    {
        var taken = new List<ServerNotification>(count);
        while (taken.Count < count) taken.Add(await NextNotification().ConfigureAwait(false));
        return taken;
    }

    private static async Task<T> Next<T>(Channel<T> channel, string what)
    {
        try
        {
            return await channel.Reader.ReadAsync().AsTask().WaitAsync(Deadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"no {what} arrived");
        }
    }
}

internal static class Assert2
{
    public static T Is<T>(object value) where T : class =>
        value as T ?? throw new Xunit.Sdk.XunitException($"expected a {typeof(T).Name}, got {value}");
}
