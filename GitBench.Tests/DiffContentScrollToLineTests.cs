using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// Scrolling the whole-file viewer to a line someone asked for. Row geometry needs measured text,
/// and text is measured on the first draw, so the interesting case is a jump asked for before the
/// view has ever been drawn — which is exactly when the file browser asks.
/// </summary>
public class DiffContentScrollToLineTests
{
    [Fact]
    public void AJumpAskedForBeforeTheFirstDrawStillLands()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(FullFile("src/long.cs"));

        view.RequestScrollToNewLine(100);
        harness.Render();

        Assert.True(view.TryGetTopVisibleNewLine(out var top));
        Assert.Equal(97, top);
    }

    [Fact]
    public void AJumpAskedForAfterTheFirstDrawLandsToo()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(FullFile("src/long.cs"));
        harness.Render();

        view.RequestScrollToNewLine(100);
        harness.Render();

        Assert.True(view.TryGetTopVisibleNewLine(out var top));
        Assert.Equal(97, top);
    }

    // A line number means nothing once the file under it has been swapped, and a jump held for a
    // view that had not drawn yet would otherwise be honoured against whatever arrived next.
    [Fact]
    public void AJumpIsDroppedWhenAnotherFileArrivesFirst()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(FullFile("src/long.cs"));

        view.RequestScrollToNewLine(100);
        view.SetRenderState(FullFile("src/other.cs"));
        harness.Render();

        Assert.True(view.TryGetTopVisibleNewLine(out var top));
        Assert.Equal(1, top);
    }

    // The breadcrumb needs this on every scroll, and can only have it once metrics resolve — so it
    // is published from the draw, not from the scroll event that precedes the first one.
    [Fact]
    public void TheTopVisibleLineIsPublishedWhenItBecomesKnowableAndWhenItMoves()
    {
        using var harness = Harness(out var view);
        var published = new List<int>();
        view.TopVisibleLineChanged += published.Add;

        view.SetRenderState(FullFile("src/long.cs"));
        harness.Render();
        Assert.Equal([1], published);

        harness.Render();
        Assert.Equal([1], published);

        view.RequestScrollToNewLine(100);
        harness.Render();

        Assert.Equal([1, 97], published);
    }

    private static DiffRenderState.FullFile FullFile(string path) => new(
        path,
        Enumerable.Range(1, 200).Select(i => "// line " + i).ToArray(),
        AddedLineNumbers: new HashSet<int>(),
        Side: DiffSide.WorkingTree,
        Truncated: false);

    private static GuiTestHarness Harness(out DiffContentView view)
    {
        DiffContentView built = null!;
        var harness = GuiTestHarness.Create(
            ctx => built = new DiffContentView(ctx),
            width: 800,
            height: 600,
            configure: Services);
        view = built;
        return harness;
    }

    private static void Services(Context ctx)
    {
        var mode = new State<ThemeMode>(ThemeMode.Dark);
        ctx.AddService(mode);
        ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(mode));
        ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
        ctx.AddService<IClipboard>(new FakeClipboard());
        ctx.AddService<IPlatformShell>(new FakeShell());
        ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
    }

    private sealed class FakeClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }

    private sealed class FakeShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }
}
