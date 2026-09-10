using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Rendering;
using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>
/// Relative images of a markdown file shown from a diff, read from the same side of the same
/// diff as the text was — the blob at the commit, the index, or the working tree.
/// </summary>
internal sealed record GitBlobImageSource(
    IGitDiffReader Git,
    Repo Repo,
    string BaseDir,
    DiffSide Side,
    bool OldSide,
    string? CommitSha,
    string? BaseSha) : IMarkdownImageSource
{
    public byte[]? Read(string path, int maxBytes) =>
        Git.GetFileBytes(Repo, path, Side, OldSide, maxBytes, CommitSha, BaseSha);
}

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
        var images = new GitBlobImageSource(
            git, repo, MarkdownImagePath.DirectoryOf(path), side, isOldSide, commitSha, baseSha);
        return new DiffRenderState.Markdown(path, render.Document, side, isOldSide, render.Truncated, images);
    }
}
