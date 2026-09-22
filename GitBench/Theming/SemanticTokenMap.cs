using GitBench.Lsp;

namespace GitBench.Theming;

/// <summary>
/// Pure mapping from a language server's token type to the <see cref="TokenColorSlot"/> it recolors
/// a name with, for the kinds of type a parser cannot tell apart. Null for everything else: a
/// server's word on a method or a local is no better than the parser's, and a slot for it would
/// only make the two disagree.
/// </summary>
/// <remarks>
/// Both spellings are listed: the protocol's standard names, which csharp-ls and most servers use,
/// and Roslyn's own classification names, which its server sends in place of them.
/// </remarks>
internal static class SemanticTokenMap
{
    private static readonly Dictionary<string, TokenColorSlot> Types = new(StringComparer.Ordinal)
    {
        ["class"] = TokenColorSlot.Type,
        ["class name"] = TokenColorSlot.Type,
        ["record class name"] = TokenColorSlot.Type,
        ["delegate name"] = TokenColorSlot.Type,
        ["module name"] = TokenColorSlot.Type,

        ["struct"] = TokenColorSlot.Struct,
        ["struct name"] = TokenColorSlot.Struct,
        ["record struct name"] = TokenColorSlot.Struct,

        ["interface"] = TokenColorSlot.Interface,
        ["interface name"] = TokenColorSlot.Interface,

        ["enum"] = TokenColorSlot.Enum,
        ["enum name"] = TokenColorSlot.Enum,

        ["typeParameter"] = TokenColorSlot.TypeParameter,
        ["type parameter name"] = TokenColorSlot.TypeParameter,
    };

    public static TokenColorSlot? Map(SemanticTokenType type) =>
        Types.TryGetValue(type.Name, out var slot) ? slot : null;
}
