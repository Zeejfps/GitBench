using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>
/// What a server said about itself when it started. Only the parts this client acts on: everything
/// else a server advertises is either assumed or asked for again when it is needed.
/// </summary>
/// <param name="PositionEncoding">
/// How the server counts a <c>character</c> offset. We ask for UTF-16 and every position in this
/// client is a UTF-16 offset, so a server that insists on something else is refused rather than
/// silently mis-addressed — clangd, for one, prefers UTF-8.
/// </param>
public sealed record ServerCapabilities(
    string? ServerName,
    string PositionEncoding,
    bool SupportsHover,
    bool SupportsDefinition)
{
    public const string Utf16 = "utf-16";

    public bool CountsPositionsAsWeDo =>
        string.Equals(PositionEncoding, Utf16, StringComparison.OrdinalIgnoreCase);

    public static readonly ILspResultReader<ServerCapabilities> Reader = new CapabilitiesReader();

    private sealed class CapabilitiesReader : ILspResultReader<ServerCapabilities>
    {
        public ServerCapabilities Read(JsonElement element)
        {
            var capabilities = element.TryGetProperty("capabilities", out var c) ? c : default;
            return new ServerCapabilities(
                ServerName: element.TryGetProperty("serverInfo", out var info)
                    && info.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : null,
                // Absent means the server never considered the question, which the specification
                // says to read as UTF-16 rather than as a disagreement.
                PositionEncoding: capabilities.ValueKind == JsonValueKind.Object
                    && capabilities.TryGetProperty("positionEncoding", out var encoding)
                    && encoding.ValueKind == JsonValueKind.String
                        ? encoding.GetString() ?? Utf16
                        : Utf16,
                SupportsHover: Advertises(capabilities, "hoverProvider"),
                SupportsDefinition: Advertises(capabilities, "definitionProvider"));
        }

        // A capability is announced either as true or as an options object; both mean yes.
        private static bool Advertises(JsonElement capabilities, string name) =>
            capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }
}

/// <summary>The exchange that has to happen before a server will answer anything.</summary>
public static class LspHandshake
{
    /// <summary>
    /// The opening request. <c>processId</c> is ours so a server orphaned by a crash can end itself;
    /// servers also exit on their input closing, and both belong here because the second is a
    /// convention rather than a guarantee.
    /// </summary>
    public static LspRequest<ServerCapabilities> Initialize(
        DocumentUri rootUri, int processId, JsonElement? initializationOptions = null) =>
        new(LspMethod.Initialize, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", processId);
            writer.WriteString("rootUri", rootUri.Value);
            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", "DiffDino");
            writer.WriteEndObject();

            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("general");
            writer.WriteStartArray("positionEncodings");
            writer.WriteStringValue(ServerCapabilities.Utf16);
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("textDocument");
            WriteMarkdownCapability(writer, "hover");
            writer.WriteStartObject("definition");
            writer.WriteBoolean("linkSupport", true);
            writer.WriteEndObject();
            writer.WriteStartObject("publishDiagnostics");
            writer.WriteBoolean("versionSupport", true);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartObject("window");
            writer.WriteBoolean("workDoneProgress", true);
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (initializationOptions is { } options)
            {
                writer.WritePropertyName("initializationOptions");
                options.WriteTo(writer);
            }

            writer.WriteEndObject();
        }, ServerCapabilities.Reader);

    /// <summary>Sent once the opening request is answered. Some servers send nothing until it
    /// arrives, so it is part of starting up rather than an acknowledgement.</summary>
    public static LspNotice Initialized() =>
        new(LspMethod.Initialized, writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });

    /// <summary>Asks the server to wind down. It replies, and only then may it be told to exit.</summary>
    public static LspRequest<Unit> Shutdown() =>
        new(LspMethod.Shutdown, writer => writer.WriteNullValue(), Unit.Reader);

    public static LspNotice Exit() => new(LspMethod.Exit, writer => writer.WriteNullValue());

    private static void WriteMarkdownCapability(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject(name);
        writer.WriteStartArray("contentFormat");
        writer.WriteStringValue("markdown");
        writer.WriteStringValue("plaintext");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>A result with nothing in it, for a request whose answer is only "yes".</summary>
public sealed record Unit
{
    public static readonly Unit Instance = new();

    public static readonly ILspResultReader<Unit> Reader = new UnitReader();

    private sealed class UnitReader : ILspResultReader<Unit>
    {
        public Unit Read(JsonElement element) => Instance;
    }
}
