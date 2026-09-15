using GitBench.Theming;

namespace GitBench.Features.Diff;

internal static class TokenSlotColors
{
    /// <summary>The theme color a token slot paints in, so diff rows and markdown code always
    /// agree; <see cref="TokenColorSlot.Default"/> is the surface's own plain text.</summary>
    public static uint Of(this DiffSyntaxStyles syntax, TokenColorSlot slot, uint plain) => slot switch
    {
        TokenColorSlot.Keyword => syntax.Keyword,
        TokenColorSlot.String => syntax.String,
        TokenColorSlot.Comment => syntax.Comment,
        TokenColorSlot.Number => syntax.Number,
        TokenColorSlot.Type => syntax.Type,
        TokenColorSlot.Function => syntax.Function,
        TokenColorSlot.Variable => syntax.Variable,
        TokenColorSlot.Operator => syntax.Operator,
        TokenColorSlot.Punctuation => syntax.Punctuation,
        TokenColorSlot.Constant => syntax.Constant,
        TokenColorSlot.Heading => syntax.Heading,
        TokenColorSlot.Emphasis => syntax.Emphasis,
        TokenColorSlot.Link => syntax.Link,
        TokenColorSlot.Code => syntax.Code,
        TokenColorSlot.Quote => syntax.Quote,
        _ => plain,
    };
}
