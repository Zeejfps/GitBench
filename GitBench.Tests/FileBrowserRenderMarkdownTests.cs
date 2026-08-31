using GitBench.Features.FileBrowser;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

public class FileBrowserRenderMarkdownTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-render-markdown-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RenderedIsWhatAMarkdownFileOpensAs()
    {
        using var browser = Browser(new FileBrowserUiState());
        Assert.True(browser.RenderMarkdown.Value);
    }

    [Fact]
    public void TogglingItIsPersisted()
    {
        using var browser = Browser(new FileBrowserUiState());

        browser.SetRenderMarkdown(false);

        Assert.False(browser.RenderMarkdown.Value);
        Assert.False(_persisted[^1].RenderMarkdown);
    }

    [Fact]
    public void ARestoredBrowserReadsTheWayItWasLeft()
    {
        using var browser = Browser(new FileBrowserUiState { RenderMarkdown = false });
        Assert.False(browser.RenderMarkdown.Value);
    }

    [Fact]
    public void OnlyAFileWithARenderedFormOffersTheToggle()
    {
        using var browser = Browser(new FileBrowserUiState());
        Assert.Null(browser.MarkdownPreview);
    }

    private FileBrowserViewModel Browser(FileBrowserUiState restored) => new(
        new Repo(Guid.NewGuid(), _dir.Path, "repo"),
        new EmptyFileSystem(),
        NoIgnoreOracle.Instance,
        _dispatcher,
        restored,
        _persisted.Add);

    private sealed class EmptyFileSystem : IFileSystemReader
    {
        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation) =>
            new DirectoryListing.Listed([]);

        public string? ResolveLinkTarget(string absolutePath) => null;
    }
}
