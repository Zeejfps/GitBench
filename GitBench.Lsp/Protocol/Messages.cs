using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>Writes the params (or result) object of one message. Hand written, so nothing reflects.</summary>
public delegate void WriteJson(Utf8JsonWriter writer);

/// <summary>Turns a server's result payload into a domain type, or throws <see cref="LspParseException"/>.</summary>
public interface ILspResultReader<out T>
{
    /// <param name="result">The result member, or an undefined element when the server omitted it.</param>
    T Read(JsonElement result);
}

/// <summary>A question for the server, carrying its own reader so the answer never leaves as raw JSON.</summary>
public sealed record LspRequest<T>(LspMethod Method, WriteJson WriteParams, ILspResultReader<T> ReadResult);

/// <summary>A statement to the server. No reply, ever.</summary>
public sealed record LspNotice(LspMethod Method, WriteJson WriteParams);

/// <summary>A notification from the server, parsed at the boundary.</summary>
public abstract record ServerNotification
{
    private ServerNotification() { }

    public sealed record Diagnostics(DocumentUri Uri, DocumentVersion? Version, IReadOnlyList<Diagnostic> Items) : ServerNotification;

    public sealed record Log(LogLevel Level, string Message) : ServerNotification;

    /// <summary>Anything this client does not model. Kept whole so it can be logged, not dropped.</summary>
    public sealed record Other(LspMethod Method, JsonElement Params) : ServerNotification;
}

public enum LogLevel { Error = 1, Warning = 2, Info = 3, Log = 4 }

public enum DiagnosticSeverity { Unspecified = 0, Error = 1, Warning = 2, Information = 3, Hint = 4 }

public sealed record Diagnostic(
    LspRange Range,
    DiagnosticSeverity Severity,
    string Message,
    string? Source = null,
    string? Code = null);

/// <summary>A question from the server. It is waiting; every one of these must be answered.</summary>
public sealed record ServerRequest(RequestId Id, LspMethod Method, JsonElement Params);

/// <summary>The answer to a <see cref="ServerRequest"/>.</summary>
public abstract record InboundReply
{
    private InboundReply() { }

    public sealed record Ok(WriteJson WriteResult) : InboundReply;

    public sealed record Error(LspErrorCode Code, string Message) : InboundReply;

    /// <summary>This client does not implement the method. Answered with MethodNotFound.</summary>
    public sealed record NotHandled : InboundReply;
}

/// <summary>Something arrived that the protocol does not account for. Reported, never swallowed.</summary>
public abstract record LspFault
{
    private LspFault() { }

    /// <summary>A reply whose id was never issued — or was issued twice.</summary>
    public sealed record UnmatchedResponse(RequestId Id) : LspFault;

    /// <summary>A frame that is not a JSON-RPC message.</summary>
    public sealed record UnreadableMessage(string Detail) : LspFault;

    /// <summary>The byte stream could not be framed. The connection stops after this.</summary>
    public sealed record FramingFailed(FrameFault Fault, string Detail) : LspFault;

    /// <summary>A handler for a server message threw. The server still got an answer.</summary>
    public sealed record HandlerFailed(LspMethod Method, string Detail) : LspFault;

    /// <summary>The stream ended. <paramref name="Clean"/> separates a shutdown from a death mid-message.</summary>
    public sealed record ConnectionEnded(string Detail, bool Clean) : LspFault;
}

/// <summary>The client's half of the conversation: what it does when the server talks first.</summary>
public interface ILspServerMessages
{
    void OnNotification(ServerNotification notification);

    Task<InboundReply> OnRequest(ServerRequest request, CancellationToken ct);

    void OnFault(LspFault fault);
}

/// <summary>Answers nothing and remembers nothing. The shape of a connection used only for requests.</summary>
public sealed class IgnoreServerMessages : ILspServerMessages
{
    public static readonly IgnoreServerMessages Instance = new();

    public void OnNotification(ServerNotification notification) { }

    public Task<InboundReply> OnRequest(ServerRequest request, CancellationToken ct) =>
        Task.FromResult<InboundReply>(new InboundReply.NotHandled());

    public void OnFault(LspFault fault) { }
}
