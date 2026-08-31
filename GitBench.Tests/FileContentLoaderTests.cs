using System.Text;
using GitBench.Features.FileBrowser;
using GitBench.Features.Markdown.Parsing;
using PngSharp.Api;
using Xunit;

namespace GitBench.Tests;

public class FileContentLoaderTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-filecontent-");

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static FilePreview Load(string path) => FileContentLoader.Load(path, CancellationToken.None);

    [Fact]
    public void TextIsSplitIntoLinesWithBothLineEndings()
    {
        var path = Write("a.txt", "one\ntwo\r\nthree\n");

        var text = Assert.IsType<FilePreview.Text>(Load(path));

        Assert.Equal(["one", "two", "three"], text.Lines);
        Assert.False(text.Truncated);
    }

    [Fact]
    public void ABinaryFileIsRefusedRatherThanRendered()
    {
        var path = Write("a.bin", [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01, 0x02, 0x03]);

        var refused = Assert.IsType<FilePreview.Unavailable>(Load(path));

        Assert.Equal(FilePreviewRefusal.Binary, refused.Reason);
    }

    [Fact]
    public void AFileOverTheHardCapIsNotReadAtAll()
    {
        var path = Path.Combine(_dir.Path, "huge.log");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            stream.SetLength(FileContentLoader.MaxPreviewBytes + 1);

        var refused = Assert.IsType<FilePreview.Unavailable>(Load(path));

        Assert.Equal(FilePreviewRefusal.TooLarge, refused.Reason);
    }

    [Fact]
    public void AFileOverTheTextCapIsShownUpToItAndSaysSo()
    {
        var line = new string('x', 99) + "\n";
        var repeats = FileContentLoader.MaxTextBytes / line.Length + 100;
        var path = Write("long.txt", string.Concat(Enumerable.Repeat(line, repeats)));

        var text = Assert.IsType<FilePreview.Text>(Load(path));

        Assert.True(text.Truncated);
        Assert.True(text.Lines.Count < repeats);
        Assert.All(text.Lines, l => Assert.Equal(99, l.Length));
    }

    [Fact]
    public void AVanishedFileSaysItIsGone()
    {
        var refused = Assert.IsType<FilePreview.Unavailable>(
            Load(Path.Combine(_dir.Path, "never-existed.txt")));

        Assert.Equal(FilePreviewRefusal.Missing, refused.Reason);
    }

    [Fact]
    public void ARealPngIsDecodedAsAPicture()
    {
        var path = Write("pixel.png", Png.EncodeToByteArray(Png.CreateRgb(2, 1, [10, 20, 30, 40, 50, 60])));

        var image = Assert.IsType<FilePreview.Image>(Load(path));

        Assert.Equal(2, image.Preview.Width);
        Assert.Equal(1, image.Preview.Height);
    }

    [Fact]
    public void APngThatIsNotOneFallsBackToWhatItActuallyIs()
    {
        var path = Write("logo.png", "version https://git-lfs.github.com/spec/v1\noid sha256:abc\n");

        var text = Assert.IsType<FilePreview.Text>(Load(path));

        Assert.Equal("version https://git-lfs.github.com/spec/v1", text.Lines[0]);
    }

    [Fact]
    public void ABinaryFileWithAPictureExtensionIsStillRefused()
    {
        var path = Write("broken.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x00, 0x00]);

        var refused = Assert.IsType<FilePreview.Unavailable>(Load(path));

        Assert.Equal(FilePreviewRefusal.Binary, refused.Reason);
    }

    [Fact]
    public void AUtf8ByteOrderMarkIsNotPartOfTheFirstLine()
    {
        var path = Write("bom.txt", [.. Encoding.UTF8.GetPreamble(), .. "hello\n"u8.ToArray()]);

        var text = Assert.IsType<FilePreview.Text>(Load(path));

        Assert.Equal(["hello"], text.Lines);
    }

    [Fact]
    public void AnEmptyFileIsTextWithNoLines()
    {
        var text = Assert.IsType<FilePreview.Text>(Load(Write("empty.txt", "")));

        Assert.Empty(text.Lines);
    }

    [Fact]
    public void ASourceFileComesBackWithSyntaxSpans()
    {
        var text = Assert.IsType<FilePreview.Text>(Load(Write("a.cs", "class A { }\n")));

        Assert.NotNull(text.Highlight);
    }

    [Fact]
    public void AMarkdownFileCarriesBothItsLinesAndItsParsedDocument()
    {
        var text = Assert.IsType<FilePreview.Text>(Load(Write("notes.md", "# Title\n\nSome prose.\n")));

        Assert.Equal(["# Title", "", "Some prose."], text.Lines);
        Assert.NotNull(text.Markdown);
        Assert.Collection(
            text.Markdown.Document.Blocks,
            block => Assert.Equal(1, Assert.IsType<HeadingBlock>(block).Level),
            block => Assert.IsType<ParagraphBlock>(block));
        Assert.False(text.Markdown.Truncated);
    }

    [Theory]
    [InlineData("notes.markdown")]
    [InlineData("NOTES.MD")]
    public void EveryMarkdownExtensionGetsADocument(string name) =>
        Assert.NotNull(Assert.IsType<FilePreview.Text>(Load(Write(name, "# Title\n"))).Markdown);

    [Fact]
    public void AFileThatIsNotMarkdownHasNoDocumentToRender() =>
        Assert.Null(Assert.IsType<FilePreview.Text>(Load(Write("Program.cs", "class C { }\n"))).Markdown);

    [Fact]
    public void PastTheLineCapTheDocumentSaysSoRatherThanParsingItAll()
    {
        var source = string.Concat(Enumerable.Repeat("paragraph\n\n", 4_000));
        var render = Assert.IsType<FilePreview.Text>(Load(Write("long.md", source))).Markdown;

        Assert.NotNull(render);
        Assert.True(render.Truncated);
        Assert.True(render.Document.Blocks.Count < 4_000);
    }
}
