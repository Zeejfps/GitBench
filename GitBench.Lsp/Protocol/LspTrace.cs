using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>Which way a message travelled.</summary>
public enum LspTraffic
{
    ToServer,
    FromServer,
}

/// <summary>
/// Somewhere one server's conversation is written down, message by message.
/// </summary>
/// <remarks>
/// A language server failure is invisible from inside the app: a server that never answers, one
/// that answers about a document nobody opened, and one that was never asked all look the same from
/// the view — nothing on screen. The wire is the only place the difference is stated, so it is the
/// one thing worth being able to read back.
/// </remarks>
public interface ILspTrace : IDisposable
{
    /// <summary>One JSON-RPC message, exactly as it went over the pipe.</summary>
    void Message(LspTraffic direction, ReadOnlyMemory<byte> payload);

    /// <summary>Something that was not a message: the process starting, a line on its error stream,
    /// a framing fault, the exit. Half of what a server has to say is said here rather than in
    /// JSON, and a trace missing it explains nothing.</summary>
    void Note(string text);
}

/// <summary>Where a connection's trace goes. One is opened per server process.</summary>
public interface ILspTraceSource
{
    /// <param name="server">The language the server is being started for, used to name the trace.</param>
    ILspTrace Open(string server);
}

/// <summary>Records nothing. What every connection gets unless a trace was asked for.</summary>
public sealed class NoLspTrace : ILspTrace, ILspTraceSource
{
    public static readonly NoLspTrace Instance = new();

    private NoLspTrace() { }

    public ILspTrace Open(string server) => this;

    public void Message(LspTraffic direction, ReadOnlyMemory<byte> payload) { }

    public void Note(string text) { }

    public void Dispose() { }
}

/// <summary>
/// The one line written above a traced message: which way it went, what it was, and the handful of
/// fields that decide whether it is the message being looked for.
/// </summary>
/// <remarks>
/// Deliberately flat and greppable. Every LSP question worth asking of a trace — "was this document
/// ever opened", "which uri did the server answer about", "which version is it talking about" —
/// is one search over these headings, without reading a single body.
/// </remarks>
public static class LspTraceHeading
{
    public static string Of(LspTraffic direction, ReadOnlyMemory<byte> payload)
    {
        var arrow = direction == LspTraffic.ToServer ? "-->" : "<--";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return $"{arrow} (not json)";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return $"{arrow} (not an object)";

            var parts = new List<string> { arrow, Kind(root) };
            if (root.TryGetProperty("id", out var id) && Scalar(id) is { } number) parts.Add($"id={number}");

            var parameters = Payload(root);
            if (Uri(parameters) is { } uri) parts.Add($"uri={uri}");
            if (Version(parameters) is { } version) parts.Add($"version={version}");
            if (Diagnostics(parameters) is { } count) parts.Add($"diagnostics={count}");

            return string.Join(' ', parts);
        }
    }

    /// <summary>A request or notification is its method; a reply is named by which half it carries,
    /// because an error to a request that was sent minutes ago is the whole story.</summary>
    private static string Kind(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            return method.GetString()!;
        return root.TryGetProperty("error", out _) ? "error" : "result";
    }

    /// <summary>Params on the way out, result on the way back: the fields below live in whichever
    /// of the two a message happens to have.</summary>
    private static JsonElement Payload(JsonElement root)
    {
        if (root.TryGetProperty("params", out var parameters)) return parameters;
        return root.TryGetProperty("result", out var result) ? result : default;
    }

    /// <summary><c>textDocument/*</c> nests it; publishDiagnostics does not; initialize calls it
    /// something else again.</summary>
    private static string? Uri(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (Text(payload, "uri") is { } direct) return direct;
        if (Text(payload, "rootUri") is { } root) return root;
        return payload.TryGetProperty("textDocument", out var document) ? Text(document, "uri") : null;
    }

    private static string? Version(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (Number(payload, "version") is { } direct) return direct;
        return payload.TryGetProperty("textDocument", out var document) ? Number(document, "version") : null;
    }

    private static string? Diagnostics(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("diagnostics", out var items)
        && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static string? Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Number(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value)
            ? Scalar(value)
            : null;

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => value.GetString(),
        _ => null,
    };
}
