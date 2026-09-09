using GitBench.Features.CodeIntel;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Infrastructure;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>The file browser's loader and the diff pane's answer the same question about where a line ends.</summary>
public class TextLineSplittingTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-linesplit-");

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("one\ntwo\nthree\n")]
    [InlineData("one\r\ntwo\r\nthree\r\n")]
    [InlineData("one\rtwo\rthree\r")]
    [InlineData("one\ntwo\r\nthree\r")]
    [InlineData("one\ntwo\nthree")]
    public void BothLoadersSplitAFileTheSameWay(string content)
    {
        Assert.Equal(["one", "two", "three"], BrowserLines(content));
        Assert.Equal(["one", "two", "three"], DiffLines(content));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("\r", 1)]
    [InlineData("\r\n", 1)]
    [InlineData("a\n\n", 2)]
    public void BothLoadersAgreeOnTheEdgesOfEmptiness(string content, int expected)
    {
        Assert.Equal(expected, BrowserLines(content).Count);
        Assert.Equal(expected, DiffLines(content).Count);
    }

    [Fact]
    public void ATrailingNewlineIsNotAnExtraRow() =>
        Assert.Equal(BrowserLines("a"), BrowserLines("a\n"));

    [Fact]
    public void AReadThatStoppedMidLineDropsTheLineItStoppedIn() =>
        Assert.Equal(["one", "two"], TextLines.Split("one\ntwo\nthr", dropLastPartialLine: true));

    [Fact]
    public void AReadThatStoppedOnABreakKeepsEveryWholeLine() =>
        Assert.Equal(["one", "two"], TextLines.Split("one\ntwo\n", dropLastPartialLine: true));

    private IReadOnlyList<string> BrowserLines(string content)
    {
        var path = Path.Combine(_dir.Path, "sample.txt");
        File.WriteAllText(path, content);
        var preview = FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None);
        return Assert.IsType<FilePreview.Text>(preview).Lines;
    }

    private static IReadOnlyList<string> DiffLines(string content)
    {
        var loader = new DiffPreviewLoader(new OneBlob(content), new NoConflicts(), new UnparsedFiles());
        var lines = loader.NewSideLines(
            new Repo(Guid.NewGuid(), "/repo", "repo"), new DiffTarget("sample.txt", DiffSide.Unstaged));
        return Assert.IsType<List<string>>(lines);
    }

    private sealed class OneBlob(string text) : IGitDiffReader
    {
        public Fetched<IReadOnlyList<FileChange>> LoadRangeFiles(Repo repo, string baseSha, string headSha)
            => throw new NotSupportedException();

        public DiffResult GetDiff(Repo repo, string path, DiffSide side, string? commitSha = null, string? baseSha = null)
            => throw new NotSupportedException();

        public string? GetFileText(Repo repo, string path, DiffSide side, bool oldSide, string? commitSha = null, string? baseSha = null)
            => text;

        public byte[]? GetFileBytes(Repo repo, string path, DiffSide side, bool oldSide, int maxBytes, string? commitSha = null, string? baseSha = null)
            => null;
    }

    private sealed class NoConflicts : IGitConflictOperations
    {
        public ConflictContext? GetConflictContext(Repo repo, string path) => null;

        public GitOutcome TakeOurs(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome TakeTheirs(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome TakeBoth(Repo repo, string path) => throw new NotSupportedException();

        public GitOutcome MarkResolved(Repo repo, string path) => throw new NotSupportedException();

        public IReadOnlyList<ConflictedPath> GetConflictedPaths(Repo repo) => throw new NotSupportedException();

        public ConflictStages? GetConflictStages(Repo repo, string path) => throw new NotSupportedException();
    }
}
