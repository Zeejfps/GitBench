using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Markdown images: path resolution against the document, the pixel-to-layout fit, which
// paragraphs draw as pictures, and the widget's contract — an image-only paragraph asks the
// loader for its src with the surface's source, shows nothing while loading, and falls back to
// the alt text as a link when the load fails or no loader is registered.
public class MarkdownImageTests
{
    // ---------- path resolution ----------

    [Theory]
    [InlineData("", "a.png", "a.png")]
    [InlineData("docs", "a.png", "docs/a.png")]
    [InlineData("docs", "./a.png", "docs/a.png")]
    [InlineData("docs/guide", "../img/a.png", "docs/img/a.png")]
    [InlineData("docs", "/img/a.png", "img/a.png")]
    [InlineData("docs", "a%20b.png?raw=true#frag", "docs/a b.png")]
    [InlineData("docs", "sub\\a.png", "docs/sub/a.png")]
    [InlineData("docs", "../a.png", "a.png")]
    public void ResolvesRelativeToTheDocumentDirectory(string baseDir, string src, string expected)
        => Assert.Equal(expected, MarkdownImagePath.Resolve(baseDir, src));

    [Theory]
    [InlineData("docs", "../../a.png")]
    [InlineData("", "../a.png")]
    [InlineData("", "")]
    [InlineData("", "C:/a.png")]
    [InlineData("", "file:///a.png")]
    [InlineData("", "/")]
    [InlineData("docs", "?raw=1")]
    public void RejectsPathsThatLeaveTheRootOrCarryAScheme(string baseDir, string src)
        => Assert.Null(MarkdownImagePath.Resolve(baseDir, src));

    [Theory]
    [InlineData("README.md", "")]
    [InlineData("docs/README.md", "docs")]
    [InlineData("docs\\sub\\README.md", "docs/sub")]
    public void DirectoryOfStripsTheFileName(string path, string expected)
        => Assert.Equal(expected, MarkdownImagePath.DirectoryOf(path));

    [Theory]
    [InlineData("https://x.com/a.png", true)]
    [InlineData("http://x.com/a.png", true)]
    [InlineData("ftp://x.com/a.png", false)]
    [InlineData("docs/a.png", false)]
    [InlineData("C:/a.png", false)]
    public void RemoteMeansHttpOrHttps(string src, bool expected)
        => Assert.Equal(expected, MarkdownImagePath.IsRemote(src, out _));

    // ---------- fit ----------

    [Theory]
    [InlineData(800, 400, 400f, 400f, 200f)]
    [InlineData(100, 50, 400f, 100f, 50f)]
    [InlineData(100, 50, 0f, 100f, 50f)]
    [InlineData(1000, 1, 300f, 300f, 1f)]
    [InlineData(0, 10, 300f, 0f, 0f)]
    public void FitShrinksToWidthAndNeverMagnifies(int w, int h, float available, float width, float height)
        => Assert.Equal((width, height), MarkdownImageView.Fit(w, h, available));

    // ---------- paragraph shape ----------

    private static IReadOnlyList<InlineRun> Runs(string markdown) => InlineParser.Parse(markdown);

    [Theory]
    [InlineData("![a](u)", true)]
    [InlineData("![a](u) ![b](v)", true)]
    [InlineData("![a](u)  \n![b](v)", true)]
    [InlineData("[![a](u)](https://x.com)", true)]
    [InlineData("see ![a](u)", false)]
    [InlineData("![a](u) `c`", false)]
    [InlineData("plain", false)]
    [InlineData("", false)]
    public void OnlyParagraphsOfNothingButImagesDrawAsPictures(string markdown, bool expected)
        => Assert.Equal(expected, MarkdownImageParagraph.IsImageOnly(Runs(markdown)));

    [Fact]
    public void HardBreaksSplitImageLines()
    {
        var lines = MarkdownImageParagraph.Lines(Runs("![a](u) ![b](v)  \n![c](w)"));
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { "u", "v" }, lines[0].Select(r => r.ImageSrc));
        Assert.Equal(new[] { "w" }, lines[1].Select(r => r.ImageSrc));
    }

    // ---------- widget ----------

    // Honors the loader contract: the callback never runs inside Load. Tests complete a request
    // by calling Pending themselves.
    private sealed class FakeLoader : IMarkdownImageLoader
    {
        public readonly List<(string Src, IMarkdownImageSource? Source)> Requests = new();
        public Action<ImageFrame?>? Pending;
        public int Cancelled;

        public IDisposable Load(string src, IMarkdownImageSource? source, Action<ImageFrame?> onLoaded)
        {
            Requests.Add((src, source));
            Pending = onLoaded;
            return new Handle(this);
        }

        private sealed class Handle(FakeLoader owner) : IDisposable
        {
            public void Dispose() => owner.Cancelled++;
        }
    }

    private sealed record FakeSource(string BaseDir) : IMarkdownImageSource
    {
        public byte[]? Read(string path, int maxBytes) => null;
    }

    private static readonly ImageFrame Frame = new(4, 2, new byte[4 * 2 * 4]);

    private static GuiTestHarness Create(string markdown, FakeLoader? loader, IMarkdownImageSource? source = null)
        => GuiTestHarness.Create(
            ctx => new MarkdownWidget { Document = new BasicMarkdownParser().Parse(markdown) }.BuildView(ctx),
            800, 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new FakeClipboard());
                ctx.AddService<IPlatformShell>(new FakeShell());
                ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
                if (loader != null) ctx.AddService<IMarkdownImageLoader>(loader);
                if (source != null) ctx.AddService<IMarkdownImageSource>(source);
            });

    private static bool DrawsText(RecordingCanvas canvas, string text) =>
        canvas.Texts.Any(t => t.Inputs.Text == text);

    [Fact]
    public void ImageParagraphAsksTheLoaderWithTheSurfaceSource()
    {
        var loader = new FakeLoader();
        var source = new FakeSource("docs");
        using var h = Create("![shot](img/a.png)", loader, source);
        h.Render();

        loader.Pending!(Frame);
        var canvas = h.Render();

        var request = Assert.Single(loader.Requests);
        Assert.Equal("img/a.png", request.Src);
        Assert.Same(source, request.Source);
        Assert.False(DrawsText(canvas, "shot"), "a loaded image draws no alt text");
    }

    [Fact]
    public void NothingShowsWhileTheImageLoads()
    {
        var loader = new FakeLoader();
        using var h = Create("![shot](a.png)", loader);

        var canvas = h.Render();

        Assert.False(DrawsText(canvas, "shot"));
        Assert.NotNull(loader.Pending);
    }

    [Fact]
    public void FailedLoadFallsBackToAltTextAsALink()
    {
        var loader = new FakeLoader();
        using var h = Create("![shot](a.png)", loader);
        h.Render();

        loader.Pending!(null);
        var canvas = h.Render();

        var alt = canvas.Texts.Single(t => t.Inputs.Text == "shot");
        Assert.Equal(ThemeStyles.Dark.Markdown.Link, alt.Inputs.Style.TextColor.Value);
    }

    [Fact]
    public void NoLoaderMeansAltTextOnly()
    {
        using var h = Create("![shot](a.png)", loader: null);

        Assert.True(DrawsText(h.Render(), "shot"));
    }

    [Fact]
    public void ImageInsideATextParagraphStaysAltText()
    {
        var loader = new FakeLoader();
        using var h = Create("see ![shot](a.png) here", loader);

        var canvas = h.Render();

        Assert.Empty(loader.Requests);
        Assert.True(canvas.Texts.Any(t => t.Inputs.Text.Contains("shot", StringComparison.Ordinal)));
    }

    [Fact]
    public void UnmountingCancelsThePendingLoad()
    {
        var loader = new FakeLoader();
        var h = Create("![shot](a.png)", loader);
        h.Render();

        h.Dispose();

        Assert.Equal(1, loader.Cancelled);
    }

    // ---------- working tree source ----------

    [Fact]
    public void WorkingTreeSourceReadsUnderTheRootOnly()
    {
        var root = Directory.CreateTempSubdirectory("md-images").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs", "img"));
            File.WriteAllBytes(Path.Combine(root, "docs", "img", "a.png"), new byte[] { 9, 8 });
            var source = WorkingTreeImageSource.For(root, Path.Combine(root, "docs", "README.md"))!;

            Assert.Equal("docs", source.BaseDir);
            Assert.Equal(new byte[] { 9, 8 }, source.Read("docs/img/a.png", 100));
            Assert.Null(source.Read("docs/img/a.png", 1));
            Assert.Null(source.Read("docs/img/missing.png", 100));
            Assert.Null(source.Read("../outside.png", 100));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkingTreeSourceIsNullForFilesOutsideTheRoot()
    {
        Assert.Null(WorkingTreeImageSource.For("/repo", "/elsewhere/README.md"));
    }
}
