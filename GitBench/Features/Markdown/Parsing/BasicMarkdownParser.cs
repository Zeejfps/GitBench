namespace GitBench.Features.Markdown.Parsing;

/// <summary>
/// Hand-rolled line-based block scanner for the GFM-flavored subset (see
/// docs/plans/markdown-renderer.md). Step 1 covers block structure only: inline content is kept
/// as a single unstyled <see cref="InlineRun"/> of raw text until the inline parser lands.
/// </summary>
internal sealed class BasicMarkdownParser : IMarkdownParser
{
    public MarkdownDocument Parse(string text)
    {
        throw new NotImplementedException();
    }
}
