using System.Text;
using System.Text.Json;

namespace GitBench.Lsp;

internal static class Json
{
    public static JsonElement Require(this JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
            throw new LspParseException($"missing '{name}'");
        return value;
    }

    public static JsonElement? Optional(this JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }

    public static string RequireString(this JsonElement owner, string name)
    {
        var value = owner.Require(name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new LspParseException($"'{name}' must be a string, was {value.ValueKind}");
    }

    public static string AsString(this JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new LspParseException($"{what} must be a string, was {value.ValueKind}");

    public static int AsCount(this JsonElement value, string what)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new LspParseException($"{what} must be a number, was {value.ValueKind}");
        if (number < 0) throw new LspParseException($"{what} cannot be negative, was {number}");
        return number;
    }

    public static DocumentUri ReadUri(JsonElement owner, string name) => DocumentUri.Parse(owner.RequireString(name));

    public static LspPosition ReadPosition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LspParseException($"a position must be an object, was {element.ValueKind}");
        return new LspPosition(
            new LspLine(element.Require("line").AsCount("a line")),
            new LspCharacter(element.Require("character").AsCount("a character offset")));
    }

    public static LspRange ReadRange(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LspParseException($"a range must be an object, was {element.ValueKind}");
        return new LspRange(ReadPosition(element.Require("start")), ReadPosition(element.Require("end")));
    }

    public static void WritePosition(Utf8JsonWriter writer, string name, LspPosition position)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("line", position.Line.Value);
        writer.WriteNumber("character", position.Character.Value);
        writer.WriteEndObject();
    }
}

public enum MarkupKind { PlainText, Markdown }

/// <summary>
/// What a server says about a symbol. The protocol carries this in three shapes — a bare string, a
/// language-tagged snippet, an array of either — plus a fourth for "nothing here". They collapse to
/// one closed type here, at the boundary, so nothing downstream has to know that.
/// </summary>
public abstract record Hover
{
    private Hover() { }

    public sealed record None : Hover;

    public sealed record Text(MarkupKind Kind, string Value, LspRange? Range) : Hover;

    public static readonly ILspResultReader<Hover> Reader = new HoverReader();

    private sealed class HoverReader : ILspResultReader<Hover>
    {
        public Hover Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new None();
            if (result.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a hover must be an object, was {result.ValueKind}");

            var contents = result.Optional("contents");
            if (contents is not { } body) return new None();

            var range = result.Optional("range") is { } r ? Json.ReadRange(r) : (LspRange?)null;

            if (body.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var element in body.EnumerateArray())
                {
                    var (_, value) = ReadOne(element);
                    if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
                }

                return parts.Count == 0
                    ? new None()
                    // Fenced snippets are markdown, so an array of them is too.
                    : new Text(MarkupKind.Markdown, string.Join("\n\n---\n\n", parts), range);
            }

            var (kind, text) = ReadOne(body);
            return string.IsNullOrWhiteSpace(text) ? new None() : new Text(kind, text, range);
        }

        private static (MarkupKind Kind, string Value) ReadOne(JsonElement element)
        {
            // A bare string is markdown by the protocol's own definition of MarkedString.
            if (element.ValueKind == JsonValueKind.String) return (MarkupKind.Markdown, element.GetString()!);

            if (element.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"hover contents must be a string or an object, was {element.ValueKind}");

            if (element.Optional("language") is { } language)
            {
                var snippet = element.RequireString("value");
                return (MarkupKind.Markdown, $"```{language.AsString("a hover language")}\n{snippet}\n```");
            }

            var kind = element.Optional("kind") is { } k && k.AsString("a markup kind") == "markdown"
                ? MarkupKind.Markdown
                // An unrecognised kind is treated as plain text: showing markup source is a smaller
                // failure than interpreting text that was never meant as markup.
                : MarkupKind.PlainText;
            return (kind, element.RequireString("value"));
        }
    }
}

/// <summary>A place in a file, as a server reports one.</summary>
public sealed record Location(DocumentUri Uri, LspRange Range);

/// <summary>Where a symbol is declared, where in that file to put the cursor, and — in the link
/// shape only — the span of the symbol that was asked about, back in the file the reader is
/// looking at.</summary>
public sealed record DefinitionLocation(
    DocumentUri Uri, LspRange Range, LspRange EnclosingRange, OptionalRange OriginRange);

/// <summary>
/// The answer to "go to definition". Three wire shapes — one location, an array of them, or an array
/// of the richer links — plus nothing. Order is the server's ranking and is preserved.
/// </summary>
public abstract record Definition
{
    private Definition() { }

    public sealed record None : Definition;

    public sealed record Targets(IReadOnlyList<DefinitionLocation> Items) : Definition;

    public static readonly ILspResultReader<Definition> Reader = new DefinitionReader();

    private sealed class DefinitionReader : ILspResultReader<Definition>
    {
        public Definition Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new None();

            if (result.ValueKind == JsonValueKind.Object) return new Targets([ReadOne(result)]);

            if (result.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"a definition must be an object, an array or null, was {result.ValueKind}");

            var targets = new List<DefinitionLocation>();
            foreach (var element in result.EnumerateArray()) targets.Add(ReadOne(element));
            return targets.Count == 0 ? new None() : new Targets(targets);
        }

        private static DefinitionLocation ReadOne(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a definition target must be an object, was {element.ValueKind}");

            // The shape is decided per element, not by the first one: an array may mix them.
            if (element.Optional("targetUri") is not null)
            {
                var uri = Json.ReadUri(element, "targetUri");
                var enclosing = Json.ReadRange(element.Require("targetRange"));
                var selection = element.Optional("targetSelectionRange") is { } s ? Json.ReadRange(s) : enclosing;
                var origin = element.Optional("originSelectionRange") is { } o
                    ? OptionalRange.Of(Json.ReadRange(o))
                    : OptionalRange.Absent;
                return new DefinitionLocation(uri, selection, enclosing, origin);
            }

            var location = Json.ReadUri(element, "uri");
            var range = Json.ReadRange(element.Require("range"));
            return new DefinitionLocation(location, range, range, OptionalRange.Absent);
        }
    }
}

/// <summary>
/// Everywhere a symbol is used. One wire shape — an array of plain locations, never the link form —
/// plus nothing, which servers spell as null and as an empty array interchangeably. Order is the
/// server's and is preserved.
/// </summary>
public abstract record References
{
    private References() { }

    public sealed record None : References;

    public sealed record Sites(IReadOnlyList<Location> Items) : References;

    public static readonly ILspResultReader<References> Reader = new ReferencesReader();

    private sealed class ReferencesReader : ILspResultReader<References>
    {
        public References Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new None();

            if (result.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"references must be an array or null, was {result.ValueKind}");

            var sites = new List<Location>();
            foreach (var element in result.EnumerateArray()) sites.Add(ReadOne(element));
            return sites.Count == 0 ? new None() : new Sites(sites);
        }

        private static Location ReadOne(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a reference must be an object, was {element.ValueKind}");

            return new Location(
                Json.ReadUri(element, "uri"), Json.ReadRange(element.Require("range")));
        }
    }
}

/// <summary>What a server says a symbol is, by the protocol's numbering. A number the protocol
/// does not define reads as <see cref="Unknown"/>.</summary>
public enum LspSymbolKind
{
    Unknown = 0,
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>One symbol a workspace search found: its name, kind, the name of what contains it, and
/// where it is declared.</summary>
public sealed record WorkspaceSymbol(string Name, LspSymbolKind Kind, string? ContainerName, Location Location);

/// <summary>
/// The answer to a workspace symbol search. Two wire shapes — the older <c>SymbolInformation</c> and
/// the newer <c>WorkspaceSymbol</c> — which differ only in what may be left out; both carry a name,
/// a kind and a location. A location without a range needs a resolve this client never advertises,
/// so an entry carrying one is left out rather than guessed at. Order is the server's.
/// </summary>
public sealed record WorkspaceSymbols(IReadOnlyList<WorkspaceSymbol> Items)
{
    public static readonly ILspResultReader<WorkspaceSymbols> Reader = new WorkspaceSymbolsReader();

    private sealed class WorkspaceSymbolsReader : ILspResultReader<WorkspaceSymbols>
    {
        public WorkspaceSymbols Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new WorkspaceSymbols([]);

            if (result.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"workspace symbols must be an array or null, was {result.ValueKind}");

            var symbols = new List<WorkspaceSymbol>();
            foreach (var element in result.EnumerateArray())
                if (ReadOne(element) is { } symbol)
                    symbols.Add(symbol);
            return new WorkspaceSymbols(symbols);
        }

        private static WorkspaceSymbol? ReadOne(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a workspace symbol must be an object, was {element.ValueKind}");

            var location = element.Require("location");
            if (location.Optional("range") is not { } range) return null;

            var container = element.Optional("containerName") is { } c ? c.AsString("a container name") : null;
            return new WorkspaceSymbol(
                element.RequireString("name"),
                KindOf(element.Require("kind")),
                string.IsNullOrEmpty(container) ? null : container,
                new Location(Json.ReadUri(location, "uri"), Json.ReadRange(range)));
        }

        private static LspSymbolKind KindOf(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                throw new LspParseException($"a symbol kind must be a number, was {value.ValueKind}");
            var kind = (LspSymbolKind)number;
            return Enum.IsDefined(kind) ? kind : LspSymbolKind.Unknown;
        }
    }
}

/// <summary>The requests this client knows how to ask, params written by hand.</summary>
public static class LspRequests
{
    public static LspRequest<Hover> Hover(DocumentUri uri, LspPosition at) =>
        new(LspMethod.Hover, writer => WriteTextDocumentPosition(writer, uri, at), Lsp.Hover.Reader);

    public static LspRequest<Definition> Definition(DocumentUri uri, LspPosition at) =>
        new(LspMethod.Definition, writer => WriteTextDocumentPosition(writer, uri, at), Lsp.Definition.Reader);

    /// <summary>
    /// Everywhere a symbol is used. <paramref name="includeDeclaration"/> is not a detail: the count
    /// a reader is shown above a declaration is of its usages, so counting the declaration itself
    /// makes every unused symbol read as used once.
    /// </summary>
    public static LspRequest<References> References(
        DocumentUri uri, LspPosition at, bool includeDeclaration) =>
        new(LspMethod.References, writer => WriteTextDocumentPosition(writer, uri, at, more =>
        {
            more.WriteStartObject("context");
            more.WriteBoolean("includeDeclaration", includeDeclaration);
            more.WriteEndObject();
        }), Lsp.References.Reader);

    /// <summary>What could be typed at a position, and why it was asked.</summary>
    public static LspRequest<LspCompletions> Completion(DocumentUri uri, LspPosition at, CompletionAsk ask) =>
        new(LspMethod.Completion, writer => WriteTextDocumentPosition(writer, uri, at, more =>
        {
            more.WriteStartObject("context");
            switch (ask)
            {
                case CompletionAsk.Requested:
                    more.WriteNumber("triggerKind", 1);
                    break;
                case CompletionAsk.TypedTrigger(var character):
                    more.WriteNumber("triggerKind", 2);
                    more.WriteString("triggerCharacter", character.ToString());
                    break;
                case CompletionAsk.ForIncomplete:
                    more.WriteNumber("triggerKind", 3);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ask), ask, null);
            }
            more.WriteEndObject();
        }), LspCompletions.Reader);

    /// <summary>One completion's documentation and detail, asked for by handing the item back whole.</summary>
    public static LspRequest<CompletionItemDocs> ResolveCompletion(CompletionItemHandle item) =>
        new(LspMethod.ResolveCompletion, writer => item.Item.WriteTo(writer), CompletionItemDocs.Reader);

    /// <summary>The overloads of the call around a position, and which parameter it is in.</summary>
    public static LspRequest<SignatureHelp> SignatureHelp(DocumentUri uri, LspPosition at, SignatureAsk ask) =>
        new(LspMethod.SignatureHelp, writer => WriteTextDocumentPosition(writer, uri, at, more =>
        {
            more.WriteStartObject("context");
            switch (ask)
            {
                case SignatureAsk.Requested:
                    more.WriteNumber("triggerKind", 1);
                    more.WriteBoolean("isRetrigger", false);
                    break;
                case SignatureAsk.TypedTrigger(var character):
                    more.WriteNumber("triggerKind", 2);
                    more.WriteString("triggerCharacter", character.ToString());
                    more.WriteBoolean("isRetrigger", false);
                    break;
                case SignatureAsk.ContentChanged:
                    more.WriteNumber("triggerKind", 3);
                    more.WriteBoolean("isRetrigger", true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ask), ask, null);
            }
            more.WriteEndObject();
        }), Lsp.SignatureHelp.Reader);

    /// <summary>What every name in a document is — a struct, an interface, a parameter — as the
    /// server's own compiler sees it, read against the legend the server announced.</summary>
    public static LspRequest<SemanticTokens> SemanticTokens(DocumentUri uri, SemanticTokensLegend legend) =>
        new(LspMethod.SemanticTokensFull, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, Lsp.SemanticTokens.ReaderFor(legend));

    /// <summary>Every symbol in the workspace matching a query, by the server's own matching.</summary>
    public static LspRequest<WorkspaceSymbols> WorkspaceSymbol(string query) =>
        new(LspMethod.WorkspaceSymbol, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteEndObject();
        }, WorkspaceSymbols.Reader);

    private static void WriteTextDocumentPosition(
        Utf8JsonWriter writer, DocumentUri uri, LspPosition at, WriteJson? more = null)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("textDocument");
        writer.WriteString("uri", uri.Value);
        writer.WriteEndObject();
        Json.WritePosition(writer, "position", at);
        more?.Invoke(writer);
        writer.WriteEndObject();
    }
}

/// <summary>The statements this client makes to a server.</summary>
public static class LspNotices
{
    public static LspNotice DidOpen(DocumentUri uri, LanguageId language, DocumentVersion version, string text) =>
        new(LspMethod.DidOpen, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri.Value);
            writer.WriteString("languageId", language.Value);
            writer.WriteNumber("version", version.Value);
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    /// <summary>The document's whole new text at a new version. Whole rather than as ranges: one
    /// shape every syncing server accepts, and nothing to drift out of step.</summary>
    public static LspNotice DidChange(DocumentUri uri, DocumentVersion version, string text) =>
        new(LspMethod.DidChange, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri.Value);
            writer.WriteNumber("version", version.Value);
            writer.WriteEndObject();
            writer.WriteStartArray("contentChanges");
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    public static LspNotice DidClose(DocumentUri uri) =>
        new(LspMethod.DidClose, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
}

internal static class ServerNotifications
{
    public static ServerNotification Read(LspMethod method, JsonElement parameters)
    {
        if (method == LspMethod.PublishDiagnostics) return ReadDiagnostics(parameters);
        return new ServerNotification.Other(method, parameters.Clone());
    }

    private static ServerNotification ReadDiagnostics(JsonElement parameters)
    {
        var uri = Json.ReadUri(parameters, "uri");
        var version = parameters.Optional("version") is { } v ? new DocumentVersion(v.AsCount("a document version")) : (DocumentVersion?)null;

        var items = new List<Diagnostic>();
        var array = parameters.Require("diagnostics");
        if (array.ValueKind != JsonValueKind.Array)
            throw new LspParseException($"'diagnostics' must be an array, was {array.ValueKind}");

        foreach (var element in array.EnumerateArray())
        {
            var severity = element.Optional("severity") is { } s
                ? s.AsCount("a severity") switch
                {
                    1 => DiagnosticSeverity.Error,
                    2 => DiagnosticSeverity.Warning,
                    3 => DiagnosticSeverity.Information,
                    4 => DiagnosticSeverity.Hint,
                    _ => DiagnosticSeverity.Unspecified,
                }
                : DiagnosticSeverity.Unspecified;

            var code = element.Optional("code") switch
            {
                { ValueKind: JsonValueKind.String } c => c.GetString(),
                { ValueKind: JsonValueKind.Number } c => c.GetRawText(),
                _ => null,
            };

            items.Add(new Diagnostic(
                Json.ReadRange(element.Require("range")),
                severity,
                element.RequireString("message"),
                element.Optional("source")?.AsString("a diagnostic source"),
                code));
        }

        return new ServerNotification.Diagnostics(uri, version, items);
    }
}
