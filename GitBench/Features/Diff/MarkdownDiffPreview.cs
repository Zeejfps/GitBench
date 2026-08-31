using GitBench.Features.Markdown;
using GitBench.Git;

namespace GitBench.Features.Diff;

internal static class MarkdownDiffPreview
{
    public static bool IsPreviewablePath(string path) => MarkdownFile.IsMarkdownPath(path);

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

        var render = MarkdownFile.Render(text);
        return new DiffRenderState.Markdown(path, render.Document, side, isOldSide, render.Truncated);
    }
}
