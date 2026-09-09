using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public class DiffSelectionRemapViewTests(CodeIntelFixture fixture)
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;

    [Fact]
    public void ASelectionBelowAFoldSurvivesCollapsingIt()
    {
        var (h, view, clipboard) = FullFileView();
        using (h)
        {
            DragFullFile(h, RowOfLine(9), 9, 15);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("Logout", clipboard.Text);

            view.SetFoldState(CollapsedLogin());

            var rect = Assert.Single(SelectionRects(h.Render()));
            Assert.True(rect.ContainsPoint(new PointF(XOfFullFileColumn(10), RowCenterY(4))),
                "the highlight followed line 9 to the row it now occupies");
            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("Logout", clipboard.Text);
        }
    }

    [Fact]
    public void ExpandingTheFoldAgainPutsTheSelectionBack()
    {
        var (h, view, clipboard) = FullFileView();
        using (h)
        {
            DragFullFile(h, RowOfLine(9), 9, 15);
            view.SetFoldState(CollapsedLogin());
            view.SetFoldState(FoldState.Open(Path));

            var rect = Assert.Single(SelectionRects(h.Render()));
            Assert.True(rect.ContainsPoint(new PointF(XOfFullFileColumn(10), RowCenterY(RowOfLine(9)))));
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("Logout", clipboard.Text);
        }
    }

    [Fact]
    public void ASelectionTheFoldSwallowedIsDroppedRatherThanMoved()
    {
        var (h, view, clipboard) = FullFileView();
        using (h)
        {
            DragFullFile(h, RowOfLine(5), 8, 13);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("Check", clipboard.Text);

            view.SetFoldState(CollapsedLogin());

            Assert.Empty(SelectionRects(h.Render()));
            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Null(clipboard.Text);
        }
    }

    [Fact]
    public void CopyingAcrossACollapsedFoldStillReInflatesTheHiddenBody()
    {
        var (h, view, clipboard) = FullFileView();
        using (h)
        {
            DragFullFile(h, RowOfLine(3), 4, RowOfLine(9), 15);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            var open = clipboard.Text;
            Assert.Contains("Check(user);", open);

            view.SetFoldState(CollapsedLogin());
            h.Render();
            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal(open, clipboard.Text);
        }
    }

    [Fact]
    public void ChangingToAnotherFileStillClearsTheSelection()
    {
        var (h, view, _) = FullFileView();
        using (h)
        {
            DragFullFile(h, RowOfLine(9), 9, 15);
            Assert.NotEmpty(SelectionRects(h.Render()));

            view.SetRenderState(FullFileState("src/Other.cs"), document: null);

            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    [Fact]
    public void ASelectionSurvivesTheHighlightReEmit()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            Drag(h, 1, 4, 1, 9);

            view.SetRenderState(new DiffRenderState.Loaded(Diff(), new DiffAnnotations(null, null, null)), document: null);

            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("alpha", clipboard.Text);
            Assert.Single(SelectionRects(h.Render()));
        }
    }

    [Fact]
    public void ASelectionBelowAGapSurvivesExpandingIt()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            Drag(h, 8, 0, 8, 5);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("fresh", clipboard.Text);

            view.SetRenderState(new DiffRenderState.Loaded(Diff(), null, ExpandedGap1()), document: null);

            var rect = Assert.Single(SelectionRects(h.Render()));
            Assert.True(rect.ContainsPoint(new PointF(XOfColumn(1), RowCenterY(10))),
                "two revealed rows above pushed line 17 from row 8 to row 10");
            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("fresh", clipboard.Text);
        }
    }

    [Fact]
    public void ExpandingAGapInsideASelectionCoversWhatItReveals()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            Drag(h, 4, 0, 6, 3);

            view.SetRenderState(new DiffRenderState.Loaded(Diff(), null, ExpandedGap1()), document: null);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("return alpha;\nline 13\nline 14\nint", clipboard.Text);
        }
    }

    [Fact]
    public void AnEndpointLeftOnAHunkBarSurvivesAReEmit()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            DragOntoTheBar(h);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("return alpha;", clipboard.Text);

            view.SetRenderState(new DiffRenderState.Loaded(Diff(), new DiffAnnotations(null, null, null)), document: null);

            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("return alpha;", clipboard.Text);
        }
    }

    [Fact]
    public void AnEndpointOnADissolvedHunkBarDoesNotSwallowTheRevealedLines()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            DragOntoTheBar(h);

            view.SetRenderState(new DiffRenderState.Loaded(Diff(), null, ExpandedGap1()), document: null);

            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Null(clipboard.Text);
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    [Fact]
    public void ASelectionSurvivesTheToggleIntoTheWholeFileView()
    {
        var (h, view, clipboard) = DiffView();
        using (h)
        {
            Drag(h, 4, 0, 4, 13);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("return alpha;", clipboard.Text);

            view.SetRenderState(WholeOfTheDiffedFile(), document: null);

            clipboard.Text = null;
            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Equal("return alpha;", clipboard.Text);
        }
    }

    private const string Path = "src/AuthService.cs";

    private static readonly string[] Source =
    [
        "class AuthService",
        "{",
        "    void Login(string user)",
        "    {",
        "        Check(user);",
        "        Issue(user);",
        "    }",
        "",
        "    void Logout() => Done();",
        "}",
    ];

    private static int RowOfLine(int line) => line - 1;

    private DiffRenderState FullFileState(string path) => new DiffRenderState.FullFile(
        path,
        Source,
        AddedLineNumbers: new HashSet<int>(),
        Side: DiffSide.WorkingTree,
        Truncated: false,
        Emphasis: null,
        Annotations: new DiffAnnotations(null, fixture.Outline(string.Join('\n', Source)), null));

    private FoldState CollapsedLogin()
    {
        var set = DiffRowSet.Build(FullFileState(Path), Loc(), FoldState.Open(Path));
        var id = set.Rows.OfType<DiffRow.Line>().Last(r => r.Fold is { Chevron: true }).Fold!.Value.Id;
        return FoldState.Open(Path).Toggled(id);
    }

    private static DiffResult Diff() => new(
        RepoId: Guid.Empty,
        Path: "file.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks:
        [
            new DiffHunk(10, 3, 10, 3, null,
            [
                new DiffLine(DiffLineKind.Context, 10, 10, "var alpha = 1;"),
                new DiffLine(DiffLineKind.Removed, 11, null, "var beta = 2;"),
                new DiffLine(DiffLineKind.Added, null, 11, "var gamma = 3;"),
                new DiffLine(DiffLineKind.Context, 12, 12, "return alpha;"),
            ]),
            new DiffHunk(16, 3, 16, 3, null,
            [
                new DiffLine(DiffLineKind.Context, 16, 16, "int x = 0;"),
                new DiffLine(DiffLineKind.Removed, 17, null, "old();"),
                new DiffLine(DiffLineKind.Added, null, 17, "fresh();"),
                new DiffLine(DiffLineKind.Context, 18, 18, "return x;"),
            ]),
        ],
        Truncated: false,
        ErrorMessage: null);

    private static ContextExpansion ExpandedGap1()
    {
        var lines = new List<string>();
        for (var n = 1; n <= 20; n++) lines.Add($"line {n}");
        return new ContextExpansion(lines, Truncated: false, new Dictionary<int, GapShown> { [1] = new(2, 0) });
    }

    private static DiffRenderState WholeOfTheDiffedFile()
    {
        var lines = new string[18];
        for (var n = 1; n <= lines.Length; n++) lines[n - 1] = $"line {n}";
        lines[9] = "var alpha = 1;";
        lines[10] = "var gamma = 3;";
        lines[11] = "return alpha;";
        return new DiffRenderState.FullFile(
            "file.cs", lines, new HashSet<int>(), DiffSide.WorkingTree, Truncated: false);
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    private (GuiTestHarness Harness, DiffContentView View, FakeClipboard Clipboard) DiffView()
    {
        var (h, view, clipboard) = Create();
        view.SetRenderState(new DiffRenderState.Loaded(Diff()), document: null);
        h.Render();
        return (h, view, clipboard);
    }

    private (GuiTestHarness Harness, DiffContentView View, FakeClipboard Clipboard) FullFileView()
    {
        var (h, view, clipboard) = Create();
        view.SetFoldState(FoldState.Open(Path));
        view.SetRenderState(FullFileState(Path), document: null);
        h.Render();
        return (h, view, clipboard);
    }

    private static (GuiTestHarness Harness, DiffContentView View, FakeClipboard Clipboard) Create()
    {
        DiffContentView view = null!;
        var clipboard = new FakeClipboard();
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(clipboard);
            });
        return (harness, view, clipboard);
    }

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));

    private static float XOfColumn(int column) =>
        DiffRowPainter.LineTextOriginX(0f, 2 * Advance + 8f, singleGutter: false) + column * Advance;

    private static float XOfFullFileColumn(int column) =>
        DiffRowPainter.LineTextOriginX(0f, 2 * Advance + 8f, singleGutter: true, foldColumn: true, glyphColumn: false)
        + column * Advance;

    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static void Drag(GuiTestHarness h, int fromRow, int fromCol, int toRow, int toCol)
    {
        h.MoveTo(XOfColumn(fromCol), RowCenterY(fromRow));
        h.Press();
        h.MoveTo(XOfColumn(toCol), RowCenterY(toRow));
        h.Release();
    }

    private static void DragOntoTheBar(GuiTestHarness h)
    {
        h.MoveTo(XOfColumn(0), RowCenterY(4));
        h.Press();
        h.MoveTo(XOfColumn(0), RowCenterY(5));
        h.Release();
    }

    private static void DragFullFile(GuiTestHarness h, int row, int fromCol, int toCol) =>
        DragFullFile(h, row, fromCol, row, toCol);

    private static void DragFullFile(GuiTestHarness h, int fromRow, int fromCol, int toRow, int toCol)
    {
        h.MoveTo(XOfFullFileColumn(fromCol), RowCenterY(fromRow));
        h.Press();
        h.MoveTo(XOfFullFileColumn(toCol), RowCenterY(toRow));
        h.Release();
    }

    private static IReadOnlyList<RectF> SelectionRects(RecordingCanvas canvas)
    {
        var color = ThemeStyles.Dark.DiffContent.SelectionBackground;
        var rects = new List<RectF>();
        foreach (var r in canvas.Rects)
            if (r.Inputs.Style.BackgroundColor == color)
                rects.Add(r.Inputs.Position);
        return rects;
    }
}
