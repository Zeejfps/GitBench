namespace GitGui;

/// <summary>
/// Pure mapping from a single TextMate scope string (e.g. <c>"keyword.control.cs"</c>) to a
/// curated <see cref="TokenColorSlot"/>. Matching is by longest dot-segment prefix, so the
/// more specific rule wins (<c>constant.numeric</c> beats <c>constant</c>); scopes with no
/// matching rule fall back to <see cref="TokenColorSlot.Default"/>.
/// </summary>
/// <remarks>
/// Reducing a token's full scope <em>list</em> to one slot (TextMate hands each token several
/// scopes, least- to most-specific) is the caller's job — see <see cref="SyntaxHighlighter"/>.
/// This type only answers "what is this one scope?".
/// </remarks>
internal static class ScopeColorMap
{
    // Rule order is irrelevant — the longest matching key wins, not the first. Keys are
    // dot-segment scope prefixes. The two punctuation.definition.* entries pull comment and
    // string delimiters into their parent color so a comment/string reads as one solid run.
    private static readonly KeyValuePair<string, TokenColorSlot>[] Rules =
    {
        new("comment", TokenColorSlot.Comment),
        new("punctuation.definition.comment", TokenColorSlot.Comment),
        new("string", TokenColorSlot.String),
        new("punctuation.definition.string", TokenColorSlot.String),
        new("constant", TokenColorSlot.Constant),
        new("constant.numeric", TokenColorSlot.Number),
        new("constant.character.numeric", TokenColorSlot.Number),
        new("keyword", TokenColorSlot.Keyword),
        new("keyword.operator", TokenColorSlot.Operator),
        new("storage", TokenColorSlot.Keyword),        // storage.type / storage.modifier: class, void, public, async
        new("entity.name.function", TokenColorSlot.Function),
        new("entity.name.type", TokenColorSlot.Type),
        new("entity.name.namespace", TokenColorSlot.Type),
        new("support.function", TokenColorSlot.Function),
        new("support.type", TokenColorSlot.Type),
        new("support.class", TokenColorSlot.Type),
        new("variable", TokenColorSlot.Variable),
        new("entity.name.variable", TokenColorSlot.Variable),
        new("punctuation", TokenColorSlot.Punctuation),
    };

    public static TokenColorSlot Map(string scope)
    {
        var best = TokenColorSlot.Default;
        var bestLen = -1;
        foreach (var rule in Rules)
        {
            if (rule.Key.Length > bestLen && IsScopePrefix(rule.Key, scope))
            {
                best = rule.Value;
                bestLen = rule.Key.Length;
            }
        }
        return best;
    }

    // True when prefix equals scope, or is a dot-delimited prefix of it: "keyword" matches
    // "keyword.control.cs" but not "keywordish".
    private static bool IsScopePrefix(string prefix, string scope)
    {
        if (!scope.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return scope.Length == prefix.Length || scope[prefix.Length] == '.';
    }
}
