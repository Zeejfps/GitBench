namespace GitBench.Lsp;

/// <summary>Raised by a boundary reader when a payload does not have the shape the protocol promises.</summary>
public sealed class LspParseException(string message) : Exception(message);

/// <summary>
/// A JSON-RPC error code. The interesting distinction is not which code it is but what the caller
/// should do about it: a server still building its index answers with an error that means "ask
/// again", and a client that treats it as failure looks broken for the first half minute.
/// </summary>
public readonly record struct LspErrorCode(int Value)
{
    public static readonly LspErrorCode ParseError = new(-32700);
    public static readonly LspErrorCode InvalidRequest = new(-32600);
    public static readonly LspErrorCode MethodNotFound = new(-32601);
    public static readonly LspErrorCode InvalidParams = new(-32602);
    public static readonly LspErrorCode InternalError = new(-32603);
    public static readonly LspErrorCode ServerNotInitialized = new(-32002);
    public static readonly LspErrorCode RequestFailed = new(-32803);
    public static readonly LspErrorCode ServerCancelled = new(-32802);
    public static readonly LspErrorCode ContentModified = new(-32801);
    public static readonly LspErrorCode RequestCancelled = new(-32800);

    /// <summary>The same question, asked again later, may well be answered.</summary>
    public bool MeansAskAgain =>
        this == ContentModified || this == ServerNotInitialized || this == ServerCancelled;

    /// <summary>The server dropped the request because we asked it to.</summary>
    public bool MeansCancelled => this == RequestCancelled;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Everything that can come of asking a server a question. One closed set, because each case needs a
/// different answer from the pane above: show it, ask again later, show an error, log a client bug,
/// keep waiting, or restart the server. An exception for any of them would put one case outside the
/// switch, which is exactly the state this type exists to prevent.
/// </summary>
public abstract record LspResponse<T>
{
    private LspResponse() { }

    /// <summary>The server answered and the answer parsed.</summary>
    public sealed record Ok(T Value) : LspResponse<T>;

    /// <summary>The server refused for a reason that will pass — indexing, a stale document version.</summary>
    public sealed record Retryable(LspErrorCode Code, string Message) : LspResponse<T>;

    /// <summary>The server refused, and asking again will not help.</summary>
    public sealed record Failed(LspErrorCode Code, string Message) : LspResponse<T>;

    /// <summary>The server answered with something this client cannot read. A bug on one side or the other.</summary>
    public sealed record Malformed(string Detail) : LspResponse<T>;

    /// <summary>No answer inside the budget. The request was cancelled server-side on the way out.</summary>
    public sealed record TimedOut(TimeSpan After) : LspResponse<T>;

    /// <summary>The caller withdrew the question, or the server confirmed it dropped it.</summary>
    public sealed record Cancelled : LspResponse<T>;

    /// <summary>The connection ended before an answer arrived.</summary>
    public sealed record Disconnected(string Reason) : LspResponse<T>;
}
