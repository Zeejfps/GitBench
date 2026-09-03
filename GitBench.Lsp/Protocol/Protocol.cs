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

/// <summary>Where a symbol is declared, where in that file to put the cursor, and — in the link
/// shape only — the span of the symbol that was asked about, back in the file the reader is
/// looking at.</summary>
public sealed record DefinitionLocation(
    DocumentUri Uri, LspRange Range, LspRange EnclosingRange, LspRange? OriginRange = null);

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
                var origin = element.Optional("originSelectionRange") is { } o ? Json.ReadRange(o) : (LspRange?)null;
                return new DefinitionLocation(uri, selection, enclosing, origin);
            }

            var location = Json.ReadUri(element, "uri");
            var range = Json.ReadRange(element.Require("range"));
            return new DefinitionLocation(location, range, range);
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

    public sealed record Sites(IReadOnlyList<Documents.Location> Items) : References;

    public static readonly ILspResultReader<References> Reader = new ReferencesReader();

    private sealed class ReferencesReader : ILspResultReader<References>
    {
        public References Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new None();

            if (result.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"references must be an array or null, was {result.ValueKind}");

            var sites = new List<Documents.Location>();
            foreach (var element in result.EnumerateArray()) sites.Add(ReadOne(element));
            return sites.Count == 0 ? new None() : new Sites(sites);
        }

        private static Documents.Location ReadOne(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a reference must be an object, was {element.ValueKind}");

            return new Documents.Location(
                Json.ReadUri(element, "uri"), Json.ReadRange(element.Require("range")));
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
        if (method == LspMethod.LogMessage) return ReadLog(parameters);
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

    private static ServerNotification ReadLog(JsonElement parameters)
    {
        var level = parameters.Optional("type") is { } t
            ? t.AsCount("a log level") switch
            {
                1 => LogLevel.Error,
                2 => LogLevel.Warning,
                3 => LogLevel.Info,
                _ => LogLevel.Log,
            }
            : LogLevel.Log;
        return new ServerNotification.Log(level, parameters.RequireString("message"));
    }
}
