using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>A token type as a server names it in its legend: <c>struct</c>, <c>interface</c>, or a
/// name of the server's own such as Roslyn's <c>struct name</c>.</summary>
public readonly record struct SemanticTokenType(string Name)
{
    public override string ToString() => Name;
}

/// <summary>The server's numbering of token types. Every token it sends names its type by an index
/// into this list, so the list is what turns the numbers back into words.</summary>
public sealed record SemanticTokensLegend(IReadOnlyList<SemanticTokenType> TokenTypes);

/// <summary>
/// Whether a server will classify a whole document. A server that offers only the range form is
/// read as not offering it: this client always wants the file, and asking a range for all of it is
/// a second request shape to keep working for no gain.
/// </summary>
public abstract record SemanticTokensSupport
{
    private SemanticTokensSupport() { }

    public static readonly SemanticTokensSupport Unsupported = new None();

    public sealed record None : SemanticTokensSupport;

    public sealed record WholeDocument(SemanticTokensLegend Legend) : SemanticTokensSupport;

    internal static SemanticTokensSupport Read(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("semanticTokensProvider", out var provider)
            || provider.ValueKind != JsonValueKind.Object)
            return Unsupported;

        var full = provider.Optional("full");
        if (full is not { ValueKind: JsonValueKind.True or JsonValueKind.Object }) return Unsupported;

        if (provider.Optional("legend") is not { } legend) return Unsupported;
        var types = legend.Require("tokenTypes");
        if (types.ValueKind != JsonValueKind.Array)
            throw new LspParseException($"'tokenTypes' must be an array, was {types.ValueKind}");

        var names = new List<SemanticTokenType>();
        foreach (var type in types.EnumerateArray()) names.Add(new SemanticTokenType(type.AsString("a token type")));
        return new WholeDocument(new SemanticTokensLegend(names));
    }
}

/// <summary>One classified span, on one line: the protocol forbids a token to cross a line
/// unless the client says it can take that, and this one does not.</summary>
public readonly record struct SemanticToken(LspLine Line, LspCharacter Start, int Length, SemanticTokenType Type);

/// <summary>
/// A document's classified spans, in document order. Decoded at the boundary against the legend
/// the server announced, so nothing past here sees the relative integer encoding.
/// </summary>
public sealed record SemanticTokens(IReadOnlyList<SemanticToken> Tokens)
{
    public static readonly SemanticTokens Empty = new([]);

    // line delta, start delta, length, type index, modifier bits.
    private const int Stride = 5;

    public static ILspResultReader<SemanticTokens> ReaderFor(SemanticTokensLegend legend) => new Reader(legend);

    private sealed class Reader(SemanticTokensLegend legend) : ILspResultReader<SemanticTokens>
    {
        public SemanticTokens Read(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return Empty;

            var data = result.Require("data");
            if (data.ValueKind != JsonValueKind.Array)
                throw new LspParseException($"'data' must be an array, was {data.ValueKind}");

            var count = data.GetArrayLength();
            if (count % Stride != 0)
                throw new LspParseException($"'data' must hold whole tokens of {Stride} numbers, had {count}");

            var tokens = new List<SemanticToken>(count / Stride);
            var numbers = new int[Stride];
            var slot = 0;
            var line = 0;
            var start = 0;
            foreach (var element in data.EnumerateArray())
            {
                numbers[slot++] = element.AsCount("a semantic token number");
                if (slot < Stride) continue;
                slot = 0;

                var (lineDelta, startDelta, length, typeIndex) = (numbers[0], numbers[1], numbers[2], numbers[3]);
                start = lineDelta == 0 ? start + startDelta : startDelta;
                line += lineDelta;

                if (typeIndex >= legend.TokenTypes.Count)
                    throw new LspParseException(
                        $"token type {typeIndex} is past the legend's {legend.TokenTypes.Count} types");

                tokens.Add(new SemanticToken(
                    new LspLine(line), new LspCharacter(start), length, legend.TokenTypes[typeIndex]));
            }

            return new SemanticTokens(tokens);
        }
    }
}
