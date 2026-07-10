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

// Drag-to-select and copy over the real DiffContentView, driven through the real input system:
// press, move, release, Ctrl+C. The synthetic measurer pins geometry (16px rows, 8px mono
// advance), so a pointer x maps to a known character column and the highlight rects can be
// checked against the columns the drag actually covered.
public class DiffSelectionViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text;
        public void SetText(string text) => Text = text;
        public string? GetText() => Text;
    }

    // Two hunks nine lines into the file, three hidden lines between them. Rows:
    //   [0] top bar (gap 0, up expander)   [5] middle bar (gap 1, 3 hidden)
    //   [1] context "var alpha = 1;"       [6] context "int x = 0;"
    //   [2] removed "var beta = 2;"        [7] removed "old();"
    //   [3] added   "var gamma = 3;"       [8] added   "fresh();"
    //   [4] context "return alpha;"        [9] context "return x;"
    //                                      [10] EOF bar
    // Line numbers reach 18, so both gutters are two digits wide.
    private static DiffResult Diff()
    {
        var first = new DiffHunk(10, 3, 10, 3, null, new[]
        {
            new DiffLine(DiffLineKind.Context, 10, 10, "var alpha = 1;"),
            new DiffLine(DiffLineKind.Removed, 11, null, "var beta = 2;"),
            new DiffLine(DiffLineKind.Added, null, 11, "var gamma = 3;"),
            new DiffLine(DiffLineKind.Context, 12, 12, "return alpha;"),
        });
        var second = new DiffHunk(16, 3, 16, 3, null, new[]
        {
            new DiffLine(DiffLineKind.Context, 16, 16, "int x = 0;"),
            new DiffLine(DiffLineKind.Removed, 17, null, "old();"),
            new DiffLine(DiffLineKind.Added, null, 17, "fresh();"),
            new DiffLine(DiffLineKind.Context, 18, 18, "return x;"),
        });
        return new DiffResult(
            RepoId: Guid.Empty,
            Path: "file.cs",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: new[] { first, second },
            Truncated: false,
            ErrorMessage: null);
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

    private static float TextOrigin()
    {
        var gutter = 2 * Advance + 8f; // two-digit line numbers
        return DiffRowPainter.LineTextOriginX(0f, gutter, singleGutter: false);
    }

    private static float XOfColumn(int column) => TextOrigin() + column * Advance;
    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static void Drag(GuiTestHarness h, int fromRow, int fromCol, int toRow, int toCol)
    {
        h.MoveTo(XOfColumn(fromCol), RowCenterY(fromRow));
        h.Press();
        h.MoveTo(XOfColumn(toCol), RowCenterY(toRow));
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

    [Fact]
    public void DraggingWithinALineHighlightsExactlyThoseColumns()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render(); // resolve font metrics

            Drag(h, fromRow: 1, fromCol: 4, toRow: 1, toCol: 9); // "alpha" in "var alpha = 1;"
            var rects = SelectionRects(h.Render());

            var rect = Assert.Single(rects);
            Assert.Equal(XOfColumn(4), rect.Left, 3);
            Assert.Equal(5 * Advance, rect.Width, 3);
        }
    }

    [Fact]
    public void DraggingWithinALineCopiesThatSubstring()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, fromRow: 1, fromCol: 4, toRow: 1, toCol: 9);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("alpha", clipboard.Text);
        }
    }

    // A press that never travels is a plain click: it must not leave a selection behind, and the
    // click still belongs to whatever sits under it.
    [Fact]
    public void ClickingWithoutDraggingSelectsNothing()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            h.Click(XOfColumn(4), RowCenterY(1));
            Assert.Empty(SelectionRects(h.Render()));

            h.PressKey(KeyboardKey.C, InputModifiers.Control);
            Assert.Null(clipboard.Text);
        }
    }

    [Fact]
    public void ClickingClearsAPriorSelection()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, 1, 0, 1, 5);
            Assert.NotEmpty(SelectionRects(h.Render()));

            h.Click(XOfColumn(2), RowCenterY(3));
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    // Dragging down the body crosses added and removed rows; the clipboard gets the code, not the
    // +/- glyphs or the line numbers drawn in the gutters beside it.
    [Fact]
    public void DraggingAcrossLinesCopiesBareCodeWithoutGuttersOrGlyphs()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, fromRow: 1, fromCol: 0, toRow: 3, toCol: 14);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("var alpha = 1;\nvar beta = 2;\nvar gamma = 3;", clipboard.Text);
        }
    }

    // A drag from one hunk into the next crosses the "@@" bar between them. The bar is a row, but
    // it is not code: the paste skips it and the lines on either side land adjacent.
    [Fact]
    public void DraggingAcrossAHunkBarOmitsTheBarFromTheCopy()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            // Row 4 is hunk 0's last line, row 5 the bar, row 6 hunk 1's first line.
            Drag(h, fromRow: 4, fromCol: 0, toRow: 6, toCol: 3);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("return alpha;\nint", clipboard.Text);
            Assert.DoesNotContain("@@", clipboard.Text);
        }
    }

    // The bar row itself carries no selectable text, so the drag passes over it unhighlighted.
    [Fact]
    public void DraggingAcrossAHunkBarHighlightsOnlyTheCodeRows()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, fromRow: 4, fromCol: 0, toRow: 6, toCol: 3);
            Assert.Equal(2, SelectionRects(h.Render()).Count);
        }
    }

    [Fact]
    public void DraggingUpwardsSelectsTheSameSpan()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, fromRow: 3, fromCol: 3, toRow: 1, toCol: 4);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("alpha = 1;\nvar beta = 2;\nvar", clipboard.Text);
        }
    }

    [Fact]
    public void DoubleClickSelectsTheWordUnderThePointer()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            var x = XOfColumn(6); // inside "alpha"
            var y = RowCenterY(1);
            h.Click(x, y);
            h.Click(x, y);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("alpha", clipboard.Text);
        }
    }

    [Fact]
    public void TripleClickSelectsTheWholeLine()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            var x = XOfColumn(6);
            var y = RowCenterY(1);
            h.Click(x, y);
            h.Click(x, y);
            h.Click(x, y);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal("var alpha = 1;", clipboard.Text);
        }
    }

    [Fact]
    public void SelectAllCopiesEveryCodeLine()
    {
        var (h, view, clipboard) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            h.MoveTo(XOfColumn(2), RowCenterY(1));
            h.PressKey(KeyboardKey.A, InputModifiers.Control);
            h.PressKey(KeyboardKey.C, InputModifiers.Control);

            Assert.Equal(
                """
                var alpha = 1;
                var beta = 2;
                var gamma = 3;
                return alpha;
                int x = 0;
                old();
                fresh();
                return x;
                """,
                clipboard.Text);
        }
    }

    [Fact]
    public void EscapeClearsTheSelection()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, 1, 0, 3, 5);
            Assert.NotEmpty(SelectionRects(h.Render()));

            h.PressKey(KeyboardKey.Escape);
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    // A gap expander owns its click; dragging off one must not smear a selection across the bar.
    [Fact]
    public void PressingAGapExpanderDoesNotStartASelection()
    {
        var (h, view, _) = Create();
        using (h)
        {
            var expanded = 0;
            view.OnExpandGap = (_, _) => expanded++;
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            // Row 5's bar bridges the 3-line gap; the whole bar is its click target.
            h.MoveTo(15f, RowCenterY(5));
            h.Press();
            h.MoveTo(XOfColumn(6), RowCenterY(7));
            h.Release();

            Assert.Equal(1, expanded);
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    // Loading a different file must not leave a selection pointing at the old file's rows.
    [Fact]
    public void ChangingTheRenderedFileClearsTheSelection()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();
            Drag(h, 1, 0, 1, 5);
            Assert.NotEmpty(SelectionRects(h.Render()));

            view.SetRenderState(new DiffRenderState.Loaded(Diff() with { Path = "other.cs" }));
            Assert.Empty(SelectionRects(h.Render()));
        }
    }

    // Interior rows of a multi-line selection are highlighted to their end plus a sliver, so the
    // block reads as continuous rather than stopping at each line's ragged right edge.
    [Fact]
    public void InteriorRowsHighlightPastEndOfLine()
    {
        var (h, view, _) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(Diff()));
            h.Render();

            Drag(h, fromRow: 1, fromCol: 0, toRow: 3, toCol: 3);
            var rects = SelectionRects(h.Render());

            Assert.Equal(3, rects.Count);
            // Row 1 is "var alpha = 1;" — 14 chars, plus the half-cell newline sliver.
            var first = rects[0];
            Assert.Equal(XOfColumn(0), first.Left, 3);
            Assert.Equal(14 * Advance + Advance / 2f, first.Width, 3);
            // The last row stops exactly at the drag's column, with no sliver.
            var last = rects[2];
            Assert.Equal(3 * Advance, last.Width, 3);
        }
    }
}
