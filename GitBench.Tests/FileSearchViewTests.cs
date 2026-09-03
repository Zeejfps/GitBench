using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Find in file where it meets the whole-file viewer: the wash over each hit, and the scroll that
/// brings one into view. The synthetic measurer pins geometry (16px rows, 8px mono advance), so a
/// hit's columns map to known pixels.
/// </summary>
public class FileSearchViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const int VisibleRows = 600 / (int)RowH;

    private const string Path = "src/long.cs";

    // Three digits of line number for a 200-line file, one gutter, and no +/- column: nothing in a
    // whole-file preview is an addition.
    private static float TextOrigin() => DiffRowPainter.LineTextOriginX(
        0f, 3 * Advance + 8f, singleGutter: true, foldColumn: false, glyphColumn: false);

    // "// hit N" on the lines named, "// line N" everywhere else.
    private static DiffRenderState.FullFile File(params int[] hitLines) => new(
        Path,
        Enumerable.Range(1, 200)
            .Select(i => hitLines.Contains(i) ? "// hit " + i : "// line " + i)
            .ToArray(),
        AddedLineNumbers: new HashSet<int>(),
        Side: DiffSide.WorkingTree,
        Truncated: false);

    private static FileSearchHits Hits(DiffRenderState.FullFile file, string query, int current) =>
        FileSearch.In(file.Path, file.Lines, new FileSearchQuery(query, false, false), new FileLine(1))
            with { Current = current };

    // What the body does with one hit list: the hits and the cursor's place in them arrive together,
    // the way the preview hands them over.
    private static void Reveal(DiffContentView view, FileSearchHits hits)
    {
        view.SetSearch(new DiffSearchOverlay(hits));
        view.RevealSearchMatch(hits.At!.Value);
    }

    private static IReadOnlyList<RectF> Washes(RecordingCanvas canvas, uint color)
    {
        var rects = new List<RectF>();
        foreach (var r in canvas.Rects)
            if (r.Inputs.Style.BackgroundColor == color)
                rects.Add(r.Inputs.Position);
        return rects;
    }

    private static IReadOnlyList<RectF> Matches(RecordingCanvas canvas) =>
        Washes(canvas, ThemeStyles.Dark.DiffContent.SearchMatchBackground);

    private static IReadOnlyList<RectF> Cursor(RecordingCanvas canvas) =>
        Washes(canvas, ThemeStyles.Dark.DiffContent.SearchCurrentBackground);

    [Fact]
    public void EveryHitOnScreenIsWashedAndTheCurrentOneDiffers()
    {
        using var harness = Harness(out var view);
        var file = File(2, 4, 6);
        view.SetRenderState(file);
        harness.Render();

        view.SetSearch(new DiffSearchOverlay(Hits(file, "hit", current: 1)));
        var canvas = harness.Render();

        Assert.Equal(2, Matches(canvas).Count);
        var cursor = Assert.Single(Cursor(canvas));
        Assert.Equal(TextOrigin() + 3 * Advance, cursor.Left, 3);
        Assert.Equal(3 * Advance, cursor.Width, 3);
    }

    // Hits belong to the file they were found in. The hit list and the rows arrive from two separate
    // bindings, so on a file switch one of them is briefly the other file's — and drawing those line
    // numbers would wash whatever now sits at them.
    [Fact]
    public void HitsFoundInAnotherFileAreNeitherDrawnNorScrolledTo()
    {
        using var harness = Harness(out var view);
        var file = File(2, 120);
        view.SetRenderState(file);
        harness.Render();

        var elsewhere = FileSearch.In(
            "src/other.cs", file.Lines, new FileSearchQuery("hit", false, false), new FileLine(120));
        view.SetSearch(new DiffSearchOverlay(elsewhere));
        view.RevealSearchMatch(elsewhere.At!.Value);
        var canvas = harness.Render();

        Assert.Empty(Matches(canvas));
        Assert.Empty(Cursor(canvas));
        Assert.Equal(new FileLine(1), view.TopVisibleNewLine());
    }

    [Fact]
    public void SteppingToAHitAlreadyOnScreenLeavesTheTextWhereItIs()
    {
        using var harness = Harness(out var view);
        var file = File(2, 5);
        view.SetRenderState(file);
        harness.Render();

        Reveal(view, Hits(file, "hit", current: 1));
        harness.Render();

        Assert.Equal(new FileLine(1), view.TopVisibleNewLine());
    }

    // Scrolled the least it could: the hit lands flush against the bottom edge. The viewport is not
    // a whole number of rows tall, so the row at the top of it is a partial one.
    private static readonly FileLine TopWhenLine120IsAtTheBottom = new(120 - VisibleRows);

    [Fact]
    public void SteppingToAHitBelowTheViewportBringsItIntoView()
    {
        using var harness = Harness(out var view);
        var file = File(120);
        view.SetRenderState(file);
        harness.Render();

        Reveal(view, Hits(file, "hit", current: 0));
        harness.Render();

        Assert.Equal(TopWhenLine120IsAtTheBottom, view.TopVisibleNewLine());
        Assert.Single(Cursor(harness.Render()));
    }

    [Fact]
    public void AHitAskedForBeforeTheFirstDrawStillLands()
    {
        using var harness = Harness(out var view);
        var file = File(120);
        view.SetRenderState(file);

        Reveal(view, Hits(file, "hit", current: 0));
        harness.Render();

        Assert.Equal(TopWhenLine120IsAtTheBottom, view.TopVisibleNewLine());
    }

    // A hit out past the right edge is otherwise scrolled to and still not on screen.
    [Fact]
    public void AHitOffToTheRightIsBroughtInHorizontallyToo()
    {
        using var harness = Harness(out var view);
        var file = new DiffRenderState.FullFile(
            Path,
            ["// " + new string('x', 400) + " needle"],
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false);
        view.SetRenderState(file);
        harness.Render();

        Assert.Empty(Cursor(harness.Render()));

        Reveal(view, FileSearch.In(
            file.Path, file.Lines, new FileSearchQuery("needle", false, false), new FileLine(1)));
        harness.Render();

        var cursor = Assert.Single(Cursor(harness.Render()));
        Assert.InRange(cursor.Left, 0f, 800f);
    }

    private static GuiTestHarness Harness(out DiffContentView view)
    {
        DiffContentView built = null!;
        var harness = GuiTestHarness.Create(
            ctx => built = new DiffContentView(ctx),
            width: 800,
            height: 600,
            configure: ctx =>
            {
                var mode = new State<ThemeMode>(ThemeMode.Dark);
                ctx.AddService(mode);
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(mode));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            });
        view = built;
        return harness;
    }
}
