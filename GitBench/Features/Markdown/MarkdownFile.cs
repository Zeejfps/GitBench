using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;

namespace GitBench.Features.Markdown;

internal sealed record MarkdownRender(MarkdownDocument Document, bool Truncated);

internal static class MarkdownFile
{
    private static readonly IMarkdownParser Parser = new BasicMarkdownParser();

    public static bool IsMarkdownPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdwn", StringComparison.OrdinalIgnoreCase);
    }

    public static MarkdownRender Render(string text, bool alreadyTruncated = false)
    {
        var (capped, truncated) = Cap(text);
        return new MarkdownRender(Parser.Parse(capped), truncated || alreadyTruncated);
    }

    private static (string Text, bool Truncated) Cap(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cut = 0;
        for (var i = 0; i < DiffOptions.TruncationLineCap; i++)
        {
            var next = normalized.IndexOf('\n', cut);
            if (next < 0) return (normalized, false);
            cut = next + 1;
        }
        return (normalized[..cut], true);
    }
}
