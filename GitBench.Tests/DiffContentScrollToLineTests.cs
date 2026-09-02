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

        view.RequestScrollToNewLine(new FileLine(100));
        harness.Render();

        Assert.Equal(new FileLine(97), view.TopVisibleNewLine());
    }

    [Fact]
    public void AJumpAskedForAfterTheFirstDrawLandsToo()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(FullFile("src/long.cs"));
        harness.Render();

        view.RequestScrollToNewLine(new FileLine(100));
        harness.Render();

        Assert.Equal(new FileLine(97), view.TopVisibleNewLine());
    }

    // A line number means nothing once the file under it has been swapped, and a jump held for a
    // view that had not drawn yet would otherwise be honoured against whatever arrived next.
    [Fact]
    public void AJumpIsDroppedWhenAnotherFileArrivesFirst()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(FullFile("src/long.cs"));

        view.RequestScrollToNewLine(new FileLine(100));
        view.SetRenderState(FullFile("src/other.cs"));
        harness.Render();

        Assert.Equal(new FileLine(1), view.TopVisibleNewLine());
    }

    // The breadcrumb needs this on every scroll, and can only have it once metrics resolve — so it
    // is published from the draw, not from the scroll event that precedes the first one.
    [Fact]
    public void TheTopVisibleLineIsPublishedWhenItBecomesKnowableAndWhenItMoves()
    {
        using var harness = Harness(out var view);
        var published = new List<FileLine?>();
        view.TopVisibleLineChanged += published.Add;

        view.SetRenderState(FullFile("src/long.cs"));
        harness.Render();
        Assert.Equal([new FileLine(1)], published);

        harness.Render();
        Assert.Equal([new FileLine(1)], published);

        view.RequestScrollToNewLine(new FileLine(100));
        harness.Render();

        Assert.Equal([new FileLine(1), new FileLine(97)], published);
    }

    // The same jump against a real diff. The three lead-in rows above the target are the gap's
    // bar/tear/bar, which stand for no line at all, so the answer can only come from the rows' own
    // numbers — counting rows would be off by exactly that chrome.
    [Fact]
    public void AJumpIntoTheSecondHunkOfADiffLandsOnItsLines()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(TwoHunks());
        harness.Render();

        view.RequestScrollToNewLine(new FileLine(60));
        harness.Render();

        Assert.Equal(new FileLine(60), view.TopVisibleNewLine());
    }

    // Nothing carries a line the gap between the hunks still hides, so the jump lands on the last
    // line above it. Asked for from further down the file, so a jump that quietly did nothing would
    // leave the reader where they were instead.
    [Fact]
    public void AJumpToALineTheGapStillHidesLandsOnTheLineAboveIt()
    {
        using var harness = Harness(out var view);
        view.SetRenderState(TwoHunks());
        view.RequestScrollToNewLine(new FileLine(100));
        harness.Render();
        Assert.NotEqual(new FileLine(1), view.TopVisibleNewLine());

        view.RequestScrollToNewLine(new FileLine(30));
        harness.Render();

        Assert.Equal(new FileLine(1), view.TopVisibleNewLine());
    }

    private static DiffRenderState.Loaded TwoHunks() => new(new DiffResult(
        RepoId: Guid.Empty,
        Path: "src/Runner.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks:
        [
            new DiffHunk(1, 3, 1, 3, null, [
                new DiffLine(DiffLineKind.Context, 1, 1, "one"),
                new DiffLine(DiffLineKind.Removed, 2, null, "two-before"),
                new DiffLine(DiffLineKind.Added, null, 2, "two-after"),
                new DiffLine(DiffLineKind.Context, 3, 3, "three"),
            ]),
            new DiffHunk(60, 50, 60, 50, null, [
                .. Enumerable.Range(60, 50)
                    .Select(n => new DiffLine(DiffLineKind.Context, n, n, "// line " + n)),
            ]),
        ],
        Truncated: false,
        ErrorMessage: null));

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
