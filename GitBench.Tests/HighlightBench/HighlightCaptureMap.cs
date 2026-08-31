using GitBench.Theming;

namespace GitBench.Tests.HighlightBench;

/// <summary>
/// Pure mapping from a tree-sitter highlight capture name (e.g. <c>"function.method"</c>) to the
/// same <see cref="TokenColorSlot"/> vocabulary <see cref="ScopeColorMap"/> resolves TextMate
/// scopes into, so the two engines can be compared slot for slot.
/// </summary>
/// <remarks>
/// Written once for every grammar rather than once per grammar: the upstream capture names are a
/// shared convention, which is the thing that makes this mapping affordable at all. Matching is by
/// longest dot-segment prefix, exactly as on the TextMate side.
/// </remarks>
internal static class HighlightCaptureMap
{
    private static readonly KeyValuePair<string, TokenColorSlot>[] Rules =
    {
        new("comment", TokenColorSlot.Comment),

        new("string", TokenColorSlot.String),
        new("character", TokenColorSlot.String),
        new("string.escape", TokenColorSlot.Constant),
        new("escape", TokenColorSlot.Constant),

        new("number", TokenColorSlot.Number),
        new("float", TokenColorSlot.Number),

        new("boolean", TokenColorSlot.Constant),
        new("constant", TokenColorSlot.Constant),
        new("constant.numeric", TokenColorSlot.Number),

        new("keyword", TokenColorSlot.Keyword),
        new("conditional", TokenColorSlot.Keyword),
        new("repeat", TokenColorSlot.Keyword),
        new("include", TokenColorSlot.Keyword),
        new("exception", TokenColorSlot.Keyword),
        new("storageclass", TokenColorSlot.Keyword),
        new("tag", TokenColorSlot.Keyword),

        new("operator", TokenColorSlot.Operator),
        new("keyword.operator", TokenColorSlot.Operator),

        new("type", TokenColorSlot.Type),
        new("constructor", TokenColorSlot.Type),
        new("module", TokenColorSlot.Type),
        new("namespace", TokenColorSlot.Type),

        new("function", TokenColorSlot.Function),
        new("method", TokenColorSlot.Function),

        new("variable", TokenColorSlot.Variable),
        new("parameter", TokenColorSlot.Variable),
        new("property", TokenColorSlot.Variable),
        new("field", TokenColorSlot.Variable),
        new("attribute", TokenColorSlot.Variable),
        new("label", TokenColorSlot.Variable),

        new("punctuation", TokenColorSlot.Punctuation),

        // Markup captures, matching the Markdown intents ScopeColorMap already resolves.
        new("text.title", TokenColorSlot.Heading),
        new("markup.heading", TokenColorSlot.Heading),
        new("text.emphasis", TokenColorSlot.Emphasis),
        new("text.strong", TokenColorSlot.Emphasis),
        new("markup.italic", TokenColorSlot.Emphasis),
        new("markup.strong", TokenColorSlot.Emphasis),
        new("text.uri", TokenColorSlot.Link),
        new("markup.link", TokenColorSlot.Link),
        new("text.literal", TokenColorSlot.Code),
        new("markup.raw", TokenColorSlot.Code),
        new("text.quote", TokenColorSlot.Quote),
        new("markup.quote", TokenColorSlot.Quote),
    };

    public static TokenColorSlot Map(string capture)
    {
        var best = TokenColorSlot.Default;
        var bestLength = -1;

        foreach (var rule in Rules)
        {
            if (rule.Key.Length > bestLength && IsCapturePrefix(rule.Key, capture))
            {
                best = rule.Value;
                bestLength = rule.Key.Length;
            }
        }

        return best;
    }

    private static bool IsCapturePrefix(string prefix, string capture)
    {
        if (!capture.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return capture.Length == prefix.Length || capture[prefix.Length] == '.';
    }
}
