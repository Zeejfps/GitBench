using System.Text;
using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>What sort of thing a completion names, as the protocol numbers them.</summary>
public enum LspCompletionKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

/// <summary>Whether a server offers completions, and which typed characters it wants to be asked
/// on — a <c>.</c> for members, say.</summary>
public abstract record CompletionSupport
{
    private CompletionSupport() { }

    public static readonly CompletionSupport Unsupported = new None();

    public sealed record None : CompletionSupport;

    /// <param name="Resolves">Whether an item's documentation and detail can be asked for one at a
    /// time, which servers that keep their lists small rely on.</param>
    public sealed record Offered(IReadOnlyList<char> TriggerCharacters, bool Resolves) : CompletionSupport;

    internal static CompletionSupport Read(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("completionProvider", out var provider)
            || provider.ValueKind is not (JsonValueKind.Object or JsonValueKind.True))
            return Unsupported;

        var triggers = new List<char>();
        if (provider.ValueKind == JsonValueKind.Object
            && provider.Optional("triggerCharacters") is { ValueKind: JsonValueKind.Array } characters)
        {
            foreach (var character in characters.EnumerateArray())
                if (character.ValueKind == JsonValueKind.String && character.GetString() is { Length: 1 } one)
                    triggers.Add(one[0]);
        }

        var resolves = provider.ValueKind == JsonValueKind.Object
            && provider.Optional("resolveProvider") is { ValueKind: JsonValueKind.True };
        return new Offered(triggers, resolves);
    }
}

/// <summary>Why a completion was asked for, which some servers answer differently: a member list
/// after a <c>.</c>, everything in scope on a request.</summary>
public abstract record CompletionAsk
{
    private CompletionAsk() { }

    public static readonly CompletionAsk Invoked = new Requested();

    public static readonly CompletionAsk Narrowing = new ForIncomplete();

    public sealed record Requested : CompletionAsk;

    public sealed record TypedTrigger(char Character) : CompletionAsk;

    /// <summary>Asked again as the prefix grew, because the last answer said it was incomplete.</summary>
    public sealed record ForIncomplete : CompletionAsk;
}

/// <summary>
/// A completion item exactly as the server sent it, kept to hand back when its documentation is
/// asked for. Opaque on purpose: servers stash whatever they need to find the item again in fields
/// this client does not read, and the protocol requires the item back whole.
/// </summary>
public sealed record CompletionItemHandle
{
    internal CompletionItemHandle(JsonElement item) => Item = item;

    internal JsonElement Item { get; }
}

/// <summary>What resolving a completion adds: its signature or type, and its documentation as
/// markdown. Either may be missing.</summary>
public sealed record CompletionItemDocs(string? Detail, string? Documentation)
{
    public static readonly ILspResultReader<CompletionItemDocs> Reader = new DocsReader();

    internal static string? DocumentationOf(JsonElement item) => item.Optional("documentation") switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Object } markup when markup.Optional("value") is { ValueKind: JsonValueKind.String } value
            => value.GetString(),
        _ => null,
    };

    private sealed class DocsReader : ILspResultReader<CompletionItemDocs>
    {
        public CompletionItemDocs Read(JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object) return new CompletionItemDocs(null, null);
            var detail = result.Optional("detail") is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
            return new CompletionItemDocs(detail, DocumentationOf(result));
        }
    }
}

/// <summary>An edit the server attached to a completion, in the text it was asked about.</summary>
public sealed record LspTextEdit(LspRange Range, string NewText);

/// <summary>
/// One completion as a server offered it. The text is always plain: a snippet's tab stops are
/// reduced to their default text at the boundary, since nothing here can step through them.
/// </summary>
/// <param name="InsertRange">Where the text goes when it is inserted — typically the prefix typed so
/// far — or null to leave that to the client.</param>
/// <param name="ReplaceRange">Where it goes when it replaces the whole word under the caret, where the
/// server said.</param>
/// <param name="AdditionalEdits">Edits elsewhere that come with it, such as the import a name needs.</param>
/// <param name="Documentation">Its documentation as markdown, where the list already carried it.</param>
/// <param name="Handle">The item as sent, for asking about it again.</param>
public sealed record LspCompletionItem(
    string Label,
    LspCompletionKind? Kind,
    string? Detail,
    string? SortText,
    string? FilterText,
    string InsertText,
    LspRange? InsertRange,
    LspRange? ReplaceRange,
    IReadOnlyList<LspTextEdit> AdditionalEdits,
    string? Documentation,
    CompletionItemHandle Handle);

/// <summary>A server's completions for one position. <see cref="IsIncomplete"/> says typing more
/// may bring others, so the list has to be asked for again rather than only narrowed.</summary>
public sealed record LspCompletions(bool IsIncomplete, IReadOnlyList<LspCompletionItem> Items)
{
    public static readonly LspCompletions Nothing = new(false, []);

    public static readonly ILspResultReader<LspCompletions> Reader = new CompletionsReader();

    private sealed class CompletionsReader : ILspResultReader<LspCompletions>
    {
        public LspCompletions Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Nothing;
            if (result.ValueKind == JsonValueKind.Array) return new(false, ReadItems(result, null));
            if (result.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"completions must be a list, an array or null, was {result.ValueKind}");

            var incomplete = result.Optional("isIncomplete") is { ValueKind: JsonValueKind.True };
            var defaults = result.Optional("itemDefaults");
            var items = result.Optional("items") is { } array ? ReadItems(array, defaults) : [];
            return new(incomplete, items);
        }

        private static IReadOnlyList<LspCompletionItem> ReadItems(JsonElement array, JsonElement? defaults)
        {
            if (array.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"completion items must be an array, was {array.ValueKind}");

            var items = new List<LspCompletionItem>();
            foreach (var element in array.EnumerateArray()) items.Add(ReadItem(element, defaults));
            return items;
        }

        private static LspCompletionItem ReadItem(JsonElement item, JsonElement? defaults)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new LspParseException($"a completion item must be an object, was {item.ValueKind}");

            var label = item.RequireString("label");
            var snippet = FormatOf(item, defaults) == 2;

            LspRange? insert = null;
            LspRange? replace = null;
            string? editText = null;
            if (item.Optional("textEdit") is { } edit)
            {
                editText = edit.RequireString("newText");
                (insert, replace) = RangesOf(edit);
            }
            else if (defaults?.Optional("editRange") is { } range)
            {
                (insert, replace) = RangesOf(range);
            }

            var text = editText
                ?? (item.Optional("insertText") is { ValueKind: JsonValueKind.String } insertText
                    ? insertText.GetString()!
                    : label);

            return new LspCompletionItem(
                label,
                KindOf(item),
                StringOrNull(item, "detail"),
                StringOrNull(item, "sortText"),
                StringOrNull(item, "filterText"),
                snippet ? Snippets.Plain(text) : text,
                insert,
                replace,
                AdditionalEditsOf(item),
                CompletionItemDocs.DocumentationOf(item),
                new CompletionItemHandle(item.Clone()));
        }

        // A plain edit has a range; an insert-or-replace edit has one of each; an editRange default
        // can be either shape too.
        private static (LspRange? Insert, LspRange? Replace) RangesOf(JsonElement edit)
        {
            if (edit.Optional("range") is { } range)
            {
                var plain = Json.ReadRange(range);
                return (plain, plain);
            }

            if (edit.ValueKind == JsonValueKind.Object && edit.TryGetProperty("start", out _))
            {
                var plain = Json.ReadRange(edit);
                return (plain, plain);
            }

            return (Json.ReadRange(edit.Require("insert")), Json.ReadRange(edit.Require("replace")));
        }

        private static int FormatOf(JsonElement item, JsonElement? defaults)
        {
            var format = item.Optional("insertTextFormat") ?? defaults?.Optional("insertTextFormat");
            return format is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var value)
                ? value
                : 1;
        }

        private static LspCompletionKind? KindOf(JsonElement item) =>
            item.Optional("kind") is { ValueKind: JsonValueKind.Number } kind
            && kind.TryGetInt32(out var value)
            && Enum.IsDefined(typeof(LspCompletionKind), value)
                ? (LspCompletionKind)value
                : null;

        private static IReadOnlyList<LspTextEdit> AdditionalEditsOf(JsonElement item)
        {
            if (item.Optional("additionalTextEdits") is not { ValueKind: JsonValueKind.Array } edits) return [];

            var list = new List<LspTextEdit>();
            foreach (var edit in edits.EnumerateArray())
                list.Add(new LspTextEdit(Json.ReadRange(edit.Require("range")), edit.RequireString("newText")));
            return list;
        }

        private static string? StringOrNull(JsonElement item, string name) =>
            item.Optional(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }
}

/// <summary>Reduces snippet syntax to the text it would insert before anything was tabbed through.</summary>
public static class Snippets
{
    /// <summary>
    /// <c>$1</c> and <c>$0</c> go, <c>${1:name}</c> becomes <c>name</c>, <c>${1|a,b|}</c> becomes
    /// <c>a</c>, and an escaped <c>\$</c>, <c>\}</c> or <c>\\</c> becomes the character itself.
    /// </summary>
    public static string Plain(string snippet)
    {
        var plain = new StringBuilder(snippet.Length);
        var i = 0;
        Append(snippet, ref i, plain, closing: false);
        return plain.ToString();
    }

    private static void Append(string s, ref int i, StringBuilder into, bool closing)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] is '$' or '}' or '\\' or ',' or '|')
            {
                into.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (closing && c == '}')
            {
                i++;
                return;
            }

            if (c != '$')
            {
                into.Append(c);
                i++;
                continue;
            }

            i++;
            if (i < s.Length && char.IsDigit(s[i]))
            {
                while (i < s.Length && char.IsDigit(s[i])) i++;
                continue;
            }

            if (i >= s.Length || s[i] != '{')
            {
                // A variable like $TM_FILENAME: nothing here can resolve it, so it inserts nothing.
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                continue;
            }

            i++;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            if (i >= s.Length) return;

            switch (s[i])
            {
                case ':':
                    i++;
                    Append(s, ref i, into, closing: true);
                    break;
                case '|':
                    i++;
                    var first = new StringBuilder();
                    while (i < s.Length && s[i] is not (',' or '|')) first.Append(s[i++]);
                    into.Append(first);
                    while (i < s.Length && s[i] != '}') i++;
                    i++;
                    break;
                default:
                    while (i < s.Length && s[i] != '}') i++;
                    i++;
                    break;
            }
        }
    }
}
