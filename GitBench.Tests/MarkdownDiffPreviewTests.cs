using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

public class MarkdownDiffPreviewTests
{
    private sealed class FakeDiffReader : IGitDiffReader
    {
        public string? NewSide;
        public string? OldSide;
        public readonly List<bool> Reads = new();

        public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha)
            => throw new NotSupportedException();

        public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null)
            => throw new InvalidOperationException("preview must not load a diff");

        public string? GetFileText(
            Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null)
        {
            Reads.Add(oldSide);
            return oldSide ? OldSide : NewSide;
        }

        public readonly List<(string Path, DiffSide Side, bool OldSide, string? Sha, string? Base)> ByteReads = new();
        public byte[]? Bytes;

        public byte[]? GetFileBytes(
            Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null,
            string? baseSha = null)
        {
            ByteReads.Add((path, side, oldSide, commitSha, baseSha));
            return Bytes;
        }
    }

    private static readonly Repo Repo = new(Guid.NewGuid(), "/tmp/repo", "repo");

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("docs/plans/renderer.MARKDOWN", true)]
    [InlineData("notes.mdown", true)]
    [InlineData("notes.mkd", true)]
    [InlineData("notes.mdwn", true)]
    [InlineData("README.md.bak", false)]
    [InlineData("Program.cs", false)]
    [InlineData("logo.png", false)]
    [InlineData("md", false)]
    public void IsPreviewablePath_matches_markdown_extensions_only(string path, bool expected)
        => Assert.Equal(expected, MarkdownDiffPreview.IsPreviewablePath(path));

    [Fact]
    public void Build_reads_relative_images_from_the_same_side_and_directory()
    {
        var git = new FakeDiffReader { NewSide = "![shot](img/a.png)\n", Bytes = new byte[] { 1, 2, 3 } };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "docs/README.md", DiffSide.Commit, "abc123", null));

        var source = Assert.IsType<GitBlobImageSource>(state.ImageSource);
        Assert.Equal("docs", source.BaseDir);
        Assert.Equal(new byte[] { 1, 2, 3 }, source.Read("docs/img/a.png", 100));
        var read = Assert.Single(git.ByteReads);
        Assert.Equal(("docs/img/a.png", DiffSide.Commit, false, "abc123", (string?)null), read);
    }

    [Fact]
    public void Build_reads_old_side_images_when_showing_the_old_side()
    {
        var git = new FakeDiffReader { OldSide = "text\n" };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Staged, null, null));

        var source = Assert.IsType<GitBlobImageSource>(state.ImageSource);
        Assert.True(source.OldSide);
        Assert.Equal(string.Empty, source.BaseDir);
    }

    [Fact]
    public void Build_parses_the_new_side_text()
    {
        var git = new FakeDiffReader { NewSide = "# Title\n\nbody text\n" };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Unstaged, null, null));

        Assert.False(state.IsOldSide);
        Assert.False(state.Truncated);
        Assert.Equal("README.md", state.Path);
        Assert.Equal(DiffSide.Unstaged, state.Side);
        Assert.Equal(new[] { false }, git.Reads);
        var heading = Assert.IsType<HeadingBlock>(state.Document.Blocks[0]);
        Assert.Equal(1, heading.Level);
    }

    [Fact]
    public void Build_falls_back_to_the_old_side_for_a_deleted_file()
    {
        var git = new FakeDiffReader { NewSide = null, OldSide = "gone but readable" };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Unstaged, null, null));

        Assert.True(state.IsOldSide);
        Assert.Single(state.Document.Blocks);
    }

    [Fact]
    public void Build_returns_null_when_neither_side_has_text()
    {
        var git = new FakeDiffReader();

        Assert.Null(MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Unstaged, null, null));
    }

    [Fact]
    public void Build_caps_an_enormous_file_and_reports_it()
    {
        var lines = string.Join("\n", Enumerable.Range(0, DiffOptions.TruncationLineCap + 500).Select(i => $"line {i}"));
        var git = new FakeDiffReader { NewSide = lines };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Unstaged, null, null));

        Assert.True(state.Truncated);
        Assert.True(state.Document.Blocks.Count > 0);
    }

    [Fact]
    public void Build_normalizes_crlf_before_parsing()
    {
        var git = new FakeDiffReader { NewSide = "# Title\r\n\r\n- one\r\n- two\r\n" };

        var state = Assert.IsType<DiffRenderState.Markdown>(
            MarkdownDiffPreview.Build(git, Repo, "README.md", DiffSide.Unstaged, null, null));

        Assert.IsType<HeadingBlock>(state.Document.Blocks[0]);
        var list = Assert.IsType<ListBlock>(state.Document.Blocks[1]);
        Assert.Equal(2, list.Items.Count);
    }
}
