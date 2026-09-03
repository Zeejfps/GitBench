using System.Text;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using Xunit;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>
/// The usages row above a declaration: where the flattener puts one, what the overlay makes it say,
/// and what a click on it does. The rows come out of the parse and the counts arrive long after, so
/// the two halves are tested apart — a row with nothing behind it yet is a state the reader will
/// spend seconds looking at.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class DiffUsageLensTests(CodeIntelFixture fixture)
{
    private const string Path = "src/AuthService.cs";

    // Lines: 1 namespace, 3 class, 5 field, 7 Login (with a body), 12 Reset (expression-bodied).
    private const string Source = """
        namespace App;

        class AuthService
        {
            int _tries;

            void Login(string user)
            {
                Check(user);
            }

            void Reset() => _tries = 0;
        }
        """;

    // ---- where the rows go ----

    [Fact]
    public void EveryDeclarationWorthCountingGetsARowDirectlyAboveIt()
    {
        var rows = Rows(usageLens: true);

        Assert.Equal(
            [(3, "App.AuthService"), (7, "App.AuthService.Login(string)"), (12, "App.AuthService.Reset()")],
            Lenses(rows).Select(l => (l.At.Value, l.Id)).ToArray());

        foreach (var lens in Lenses(rows))
        {
            var index = RowIndexOf(rows, lens);
            Assert.Equal(new FileLine(lens.At.Value), ((DiffRow.Line)rows[index + 1]).NewNumber.Line);
        }
    }

    // Fields, enum members and namespaces are noise at this density — the same call IntelliJ makes.
    [Fact]
    public void FieldsAndNamespacesGetNoRow()
    {
        var lines = Lenses(Rows(usageLens: true)).Select(l => l.At.Value).ToArray();

        Assert.DoesNotContain(1, lines); // namespace App
        Assert.DoesNotContain(5, lines); // int _tries
    }

    // The lens sits at the declaration's own indent, so it reads as a note on the signature under
    // it rather than as something in the margin.
    [Fact]
    public void ARowStartsWhereItsDeclarationDoes()
    {
        var byLine = Lenses(Rows(usageLens: true)).ToDictionary(l => l.At.Value, l => l.Indent);

        Assert.Equal(0, byLine[3]);
        Assert.Equal(4, byLine[7]);
    }

    [Fact]
    public void WithTheFeatureOffNoRowIsEmitted()
    {
        Assert.Empty(Lenses(Rows(usageLens: false)));
    }

    // A language server knows the working tree, not a commit, so the review window's row set — which
    // asks for neither folds nor lenses — must come out exactly as it did.
    [Fact]
    public void ARowSetBuiltWithoutAskingForLensesHasNone()
    {
        var set = DiffRowSet.Build(FullFile(), Loc());

        Assert.Empty(set.Rows.OfType<DiffRow.Lens>());
    }

    // Falls out of collecting lenses in the fold walk: a collapsed node's children are never
    // reached, so its members have no rows rather than hidden ones.
    [Fact]
    public void ACollapsedDeclarationShowsNoRowsForItsMembers()
    {
        var open = Lenses(Rows(usageLens: true, FoldState.Open(Path)));
        Assert.Equal(3, open.Count);

        var rows = Rows(usageLens: true, FoldState.Open(Path).Toggled("App.AuthService"));

        var lens = Assert.Single(Lenses(rows));
        Assert.Equal("App.AuthService", lens.Id);
    }

    // ---- what the row says ----

    [Theory]
    [InlineData(3, "3 usages")]
    [InlineData(1, "1 usage")]
    [InlineData(0, "no usages")]
    public void AnAnsweredRowSaysHowManyPlacesUseIt(int count, string expected)
    {
        Assert.Equal(expected, Drawn(new UsageLensState.Count(count)));
    }

    [Fact]
    public void AnUnansweredRowSaysSoRatherThanSayingZero()
    {
        Assert.Equal("…", Drawn(new UsageLensState.Asking()));
    }

    // "Cannot answer" is not "nobody uses this", and a row that conflated them would be a lie about
    // the code in every file whose server has no references support.
    [Fact]
    public void AServerThatDoesNotAnswerTheQuestionSaysThatInstead()
    {
        Assert.Equal("usages unavailable", Drawn(new UsageLensState.Unsupported()));
    }

    [Fact]
    public void ARowNothingHasBeenAskedAboutDrawsNothing()
    {
        var (h, view, _) = View();
        using (h)
        {
            view.SetUsageLens(UsageLensOverlay.Empty);

            Assert.Null(LensTextOn(h.Render()));
        }
    }

    // ---- the click ----

    [Fact]
    public void ClickingARowNamesItsDeclarationAndStartsNoSelection()
    {
        var (h, view, rows) = View();
        using (h)
        {
            var asked = new List<UsageLensTarget>();
            view.UsageLensActivated += (target, _) => asked.Add(target);
            view.SetUsageLens(Overlay(new UsageLensState.Count(3)));
            h.Render();

            var lens = Lenses(rows)[1]; // Login
            h.Click(LensX(lens, offset: 4f), RowCenterY(rows, RowIndexOf(rows, lens)));

            // The name position travels with the row: "Login" on line 7, past the four spaces of
            // indent and "void ". It is what a server is asked about, and asking at the start of
            // the line would ask about the keyword instead.
            Assert.Equal(
                [new UsageLensTarget(
                    "App.AuthService.Login(string)", new FileLine(7), new FileLine(7), new RawColumn(9))],
                asked);
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    // Only the words are the target. A lens row is as wide as the file, and the empty margin beside
    // one belongs to the selection like any other blank space.
    [Fact]
    public void ClickingBesideTheWordsIsNotAClickOnThem()
    {
        var (h, view, rows) = View();
        using (h)
        {
            var asked = 0;
            view.UsageLensActivated += (_, _) => asked++;
            view.SetUsageLens(Overlay(new UsageLensState.Count(3)));
            h.Render();

            var lens = Lenses(rows)[1];
            h.Click(LensX(lens, offset: 400f), RowCenterY(rows, RowIndexOf(rows, lens)));

            Assert.Equal(0, asked);
        }
    }

    // ---- what the rest of the body does with the rows ----

    // The row is chrome: it has no characters of the file on it, so nothing that reads the file's
    // text through the row stream may pick anything up from it.
    [Fact]
    public void SelectingTheWholeFileCopiesItsLinesAndNothingFromTheRows()
    {
        var rows = Rows(usageLens: true);
        var span = DiffSelectionModel.WholeSpan(rows);
        Assert.NotNull(span);

        var text = DiffSelectionModel.BuildCopyText(rows, span.Value.Start, span.Value.End);

        Assert.Equal(Source.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void AQuoteOverTheRowsCarriesOnlyTheCodeAndItsRealLineNumbers()
    {
        var rows = Rows(usageLens: true);
        var span = DiffSelectionModel.WholeSpan(rows);
        Assert.NotNull(span);

        var quote = DiffSelectionQuote.Build(rows, span.Value.Start, span.Value.End, Path);

        Assert.NotNull(quote);
        Assert.Equal(new FileLine(1), quote.StartLine);
        Assert.Equal(new FileLine(13), quote.EndLine);
        Assert.Equal(Source.ReplaceLineEndings("\n"), quote.Text);
    }

    // Rows are no longer all one height and no longer all lines, so a scroll target is a row offset
    // rather than a product — and the line it lands on is a row's own number, not a row count.
    [Fact]
    public void AScrollToALineStillLandsOnItWithRowsInTheStream()
    {
        using var harness = Harness(out var view);
        view.UsageLensRows = true;
        view.SetRenderState(Generated());
        harness.Render();

        // Method 8 declares on line 35; three rows above it is method 7's "Use();" on line 33.
        view.RequestScrollToNewLine(new FileLine(35));
        harness.Render();

        Assert.Equal(new FileLine(33), view.TopVisibleNewLine());
    }

    // ---- the row set ----

    private IReadOnlyList<DiffRow> Rows(bool usageLens, FoldState? folds = null) =>
        DiffRowSet.Build(FullFile(), Loc(), folds, usageLens).Rows;

    private static IReadOnlyList<DiffRow.Lens> Lenses(IReadOnlyList<DiffRow> rows) =>
        rows.OfType<DiffRow.Lens>().ToArray();

    private static int RowIndexOf(IReadOnlyList<DiffRow> rows, DiffRow row)
    {
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], row)) return i;
        throw new InvalidOperationException("That row is not in this stream.");
    }

    private DiffRenderState.FullFile FullFile() => new(
        Path,
        Source.ReplaceLineEndings("\n").Split('\n'),
        AddedLineNumbers: new HashSet<int>(),
        Side: DiffSide.WorkingTree,
        Truncated: false,
        Emphasis: null,
        Annotations: new DiffAnnotations(null, fixture.Outline(Source), null));

    // A class of twenty four-line methods: each declares on line 3 + 4k, and each contributes one
    // lens row and four line rows, so a row index far down the file is still worked out by hand.
    private DiffRenderState.FullFile Generated()
    {
        var source = new StringBuilder("class Big\n{\n");
        for (var i = 0; i < 20; i++)
            source.Append($"    void M{i}()\n    {{\n        Use();\n    }}\n");
        source.Append('}');
        var text = source.ToString();

        return new DiffRenderState.FullFile(
            "src/Big.cs",
            text.Split('\n'),
            AddedLineNumbers: new HashSet<int>(),
            Side: DiffSide.WorkingTree,
            Truncated: false,
            Emphasis: null,
            Annotations: new DiffAnnotations(null, fixture.Outline(text), null));
    }

    // ---- the view ----

    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;

    // What the lens on the class declaration draws, with the overlay saying one thing about it.
    private string? Drawn(UsageLensState state)
    {
        var (h, view, _) = View();
        using (h)
        {
            view.SetUsageLens(Overlay(state));
            return LensTextOn(h.Render());
        }
    }

    private (GuiTestHarness Harness, DiffContentView View, IReadOnlyList<DiffRow> Rows) View()
    {
        var harness = Harness(out var view);
        view.UsageLensRows = true;
        view.SetRenderState(FullFile());
        harness.Render(); // resolve font metrics
        return (harness, view, Rows(usageLens: true));
    }

    private static UsageLensOverlay Overlay(UsageLensState state) => new(
        Path,
        new Dictionary<FileLine, UsageLensState>
        {
            [new FileLine(3)] = state,
            [new FileLine(7)] = state,
            [new FileLine(12)] = state,
        });

    // The lens labels are the only proportional text the body draws, so they are found by not being
    // the monospace grid rather than by matching a string.
    private static string? LensTextOn(RecordingCanvas canvas)
    {
        var texts = canvas.Texts
            .Where(t => t.Inputs.Style.FontFamily != DiffRowPainter.MonoMetricsStyle.FontFamily)
            .Select(t => t.Inputs.Text)
            .Distinct()
            .ToList();
        return texts.Count == 0 ? null : Assert.Single(texts);
    }

    private static float TextOrigin() => DiffRowPainter.LineTextOriginX(
        0f, 2 * Advance + 8f, singleGutter: true, foldColumn: false, glyphColumn: false);

    private static float LensX(DiffRow.Lens lens, float offset) =>
        TextOrigin() + lens.Indent * Advance + offset;

    private static float RowCenterY(IReadOnlyList<DiffRow> rows, int index)
    {
        var top = Top;
        for (var i = 0; i < index; i++) top -= DiffRowMetrics.HeightOf(rows[i], RowH);
        return top - DiffRowMetrics.HeightOf(rows[index], RowH) / 2f;
    }

    private static IReadOnlyList<RectF> SelectionRects(RecordingCanvas canvas) => canvas.Rects
        .Where(r => r.Inputs.Style.BackgroundColor == ThemeStyles.Dark.DiffContent.SelectionBackground)
        .Select(r => r.Inputs.Position)
        .ToList();

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
        ctx.AddService<IClipboard>(new NullClipboard());
        ctx.AddService<IUiDispatcher>(new QueuedDispatcher());
    }

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private sealed class NullClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }
}
