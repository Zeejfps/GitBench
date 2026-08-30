using GitBench.Features.Markdown.Parsing;
using GitBench.Git;

namespace GitBench.Features.Diff;

internal static class MarkdownDiffPreview
{
    private static readonly IMarkdownParser Parser = new BasicMarkdownParser();

    public static bool IsPreviewablePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdwn", StringComparison.OrdinalIgnoreCase);
    }

    public static DiffRenderState? Build(
        IGitDiffReader git, Repo repo, string path, DiffSide side, string? commitSha, string? baseSha)
    {
        var isOldSide = false;
        var text = git.GetFileText(repo, path, side, oldSide: false, commitSha, baseSha);
        if (text == null)
        {
            text = git.GetFileText(repo, path, side, oldSide: true, commitSha, baseSha);
            isOldSide = true;
        }
        if (text == null) return null;

        var (capped, truncated) = Cap(text);
        return new DiffRenderState.Markdown(path, Parser.Parse(capped), side, isOldSide, truncated);
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
