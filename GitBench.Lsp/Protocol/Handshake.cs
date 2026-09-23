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
/// <summary>Whether a server follows a document's edits, as its <c>textDocumentSync</c> says. A
/// server that says nothing follows none, so the document is reopened rather than changed.</summary>
public enum TextSync
{
    None,
    Full,
    Incremental,
}

public sealed record ServerCapabilities(
    string? ServerName,
    string PositionEncoding,
    bool SupportsHover,
    bool SupportsDefinition,
    bool SupportsReferences)
{
    public const string Utf16 = "utf-16";

    public SemanticTokensSupport SemanticTokens { get; init; } = SemanticTokensSupport.Unsupported;

    public TextSync TextSync { get; init; } = TextSync.None;

    public CompletionSupport Completion { get; init; } = CompletionSupport.Unsupported;

    public SignatureHelpSupport SignatureHelp { get; init; } = SignatureHelpSupport.Unsupported;

    /// <summary>Whether an edited document is sent as a change rather than closed and reopened.</summary>
    public bool FollowsEdits => TextSync is TextSync.Full or TextSync.Incremental;

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
                SupportsDefinition: Advertises(capabilities, "definitionProvider"),
                SupportsReferences: Advertises(capabilities, "referencesProvider"))
            {
                SemanticTokens = SemanticTokensSupport.Read(capabilities),
                TextSync = ReadTextSync(capabilities),
                Completion = CompletionSupport.Read(capabilities),
                SignatureHelp = SignatureHelpSupport.Read(capabilities),
            };
        }

        // Either the kind itself, or an options object carrying it as "change".
        private static TextSync ReadTextSync(JsonElement capabilities)
        {
            if (capabilities.ValueKind != JsonValueKind.Object
                || !capabilities.TryGetProperty("textDocumentSync", out var sync))
                return TextSync.None;

            var kind = sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("change", out var change)
                ? change
                : sync;
            if (kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out var value)) return TextSync.None;
            return value switch
            {
                1 => TextSync.Full,
                2 => TextSync.Incremental,
                _ => TextSync.None,
            };
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
            // Announced with nothing in it. The only thing the protocol lets a client say here is
            // that it registers for references dynamically, which this one does not.
            writer.WriteStartObject("references");
            writer.WriteEndObject();
            WriteSemanticTokensCapability(writer);
            WriteCompletionCapability(writer);
            WriteSignatureHelpCapability(writer);
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

    /// <summary>
    /// Whole-document requests only, in the one encoding the protocol defines. The standard types
    /// are listed because a server may leave out any type its client did not name; the modifiers
    /// are left empty because nothing here is colored by them.
    /// </summary>
    // Snippets are declined: nothing here steps through tab stops, and a server that sends one anyway
    // has it reduced to plain text. Insert-and-replace ranges are what let Tab replace the rest of
    // the word while Enter only replaces what was typed.
    // Offsets are asked for so a parameter is found in its label by position rather than by searching
    // for a name that may appear twice.
    private static void WriteSignatureHelpCapability(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("signatureHelp");
        writer.WriteBoolean("contextSupport", true);
        writer.WriteStartObject("signatureInformation");
        writer.WriteStartArray("documentationFormat");
        writer.WriteStringValue("plaintext");
        writer.WriteEndArray();
        writer.WriteStartObject("parameterInformation");
        writer.WriteBoolean("labelOffsetSupport", true);
        writer.WriteEndObject();
        writer.WriteBoolean("activeParameterSupport", true);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteCompletionCapability(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("completion");
        writer.WriteBoolean("contextSupport", true);
        writer.WriteStartObject("completionItem");
        writer.WriteBoolean("snippetSupport", false);
        writer.WriteBoolean("insertReplaceSupport", true);
        writer.WriteStartArray("documentationFormat");
        writer.WriteStringValue("markdown");
        writer.WriteStringValue("plaintext");
        writer.WriteEndArray();
        writer.WriteStartObject("resolveSupport");
        writer.WriteStartArray("properties");
        writer.WriteStringValue("documentation");
        writer.WriteStringValue("detail");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("completionList");
        writer.WriteStartArray("itemDefaults");
        writer.WriteStringValue("editRange");
        writer.WriteStringValue("insertTextFormat");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteSemanticTokensCapability(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("semanticTokens");
        writer.WriteStartObject("requests");
        writer.WriteBoolean("full", true);
        writer.WriteEndObject();
        writer.WriteStartArray("tokenTypes");
        foreach (var type in StandardTokenTypes) writer.WriteStringValue(type);
        writer.WriteEndArray();
        writer.WriteStartArray("tokenModifiers");
        writer.WriteEndArray();
        writer.WriteStartArray("formats");
        writer.WriteStringValue("relative");
        writer.WriteEndArray();
        writer.WriteBoolean("overlappingTokenSupport", false);
        writer.WriteBoolean("multilineTokenSupport", false);
        writer.WriteEndObject();
    }

    private static readonly string[] StandardTokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter",
        "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword",
        "modifier", "comment", "string", "number", "regexp", "operator", "decorator",
    ];

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
