using GitBench.Features.Diff;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// Text selection over a diff. The row stream mixes code lines with banners and hunk bars, so the
// span math has to skip the latter without letting a drag across them break the selection — and
// the clipboard must carry the code as it appears in the file, never the gutters or the +/- glyph.
public class DiffTextSelectionTests
{
    private static DiffRow.Line Line(string text, DiffLineKind kind = DiffLineKind.Context)
        => new(kind, "1", "1", text, DiffText.VisualCells(text));

    private static DiffRow Bar() => new DiffRow.HunkSeparator("@@ -1,2 +1,2 @@", null);

    private static DiffTextPos At(int row, int ch) => new(row, ch);

    // ---- copy text ----

    [Fact]
    public void CopyWithinOneLineTakesTheSlice()
    {
        var rows = new List<DiffRow> { Line("var total = Compute();") };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 4), At(0, 9));
        Assert.Equal("total", text);
    }

    [Fact]
    public void CopyAcrossLinesJoinsWithNewlines()
    {
        var rows = new List<DiffRow> { Line("alpha"), Line("beta"), Line("gamma") };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 2), At(2, 3));
        Assert.Equal("pha\nbeta\ngam", text);
    }

    // The whole point of selecting a diff: paste the code, not the "@@" bar the drag crossed.
    [Fact]
    public void CopySkipsNonLineRows()
    {
        var rows = new List<DiffRow> { Line("before"), Bar(), Line("after") };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 0), At(2, 5));
        Assert.Equal("before\nafter", text);
    }

    [Fact]
    public void CopyIncludesEmptyLinesInTheMiddle()
    {
        var rows = new List<DiffRow> { Line("a"), Line(""), Line("b") };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 0), At(2, 1));
        Assert.Equal("a\n\nb", text);
    }

    // Added and removed rows both carry plain text; the glyph lives in the gutter, not the string.
    [Fact]
    public void CopyKeepsNeitherLineNumbersNorKindGlyph()
    {
        var rows = new List<DiffRow>
        {
            Line("    removed();", DiffLineKind.Removed),
            Line("    added();", DiffLineKind.Added),
        };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 0), At(1, 12));
        Assert.Equal("    removed();\n    added();", text);
    }

    // A selection captured before an async highlight re-emit can outlive a shorter row.
    [Fact]
    public void CopyClampsPositionsPastEndOfLine()
    {
        var rows = new List<DiffRow> { Line("ab") };
        var text = DiffSelectionModel.BuildCopyText(rows, At(0, 0), At(0, 99));
        Assert.Equal("ab", text);
    }

    // ---- row spans ----

    [Fact]
    public void RowSpanCoversTheInteriorRowsWhole()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(0, 3), At(2, 2));

        Assert.True(selection.TryRowSpan(null, 0, textLength: 8, out var first));
        Assert.Equal(new DiffRowSelection(3, 8, IncludesEol: true), first);

        Assert.True(selection.TryRowSpan(null, 1, textLength: 5, out var middle));
        Assert.Equal(new DiffRowSelection(0, 5, IncludesEol: true), middle);

        Assert.True(selection.TryRowSpan(null, 2, textLength: 9, out var last));
        Assert.Equal(new DiffRowSelection(0, 2, IncludesEol: false), last);
    }

    [Fact]
    public void RowSpanRejectsRowsOutsideTheSelection()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(1, 0), At(1, 4));
        Assert.False(selection.TryRowSpan(null, 0, 4, out _));
        Assert.False(selection.TryRowSpan(null, 2, 4, out _));
    }

    // An empty line swallowed by a multi-line selection still shows a sliver, so the highlight
    // reads as continuous down the block.
    [Fact]
    public void RowSpanOnAnEmptyInteriorRowIncludesTheNewline()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange(null, At(0, 0), At(2, 1));
        Assert.True(selection.TryRowSpan(null, 1, textLength: 0, out var span));
        Assert.Equal(new DiffRowSelection(0, 0, IncludesEol: true), span);
    }

    [Fact]
    public void CollapsedSelectionSpansNothing()
    {
        var selection = new DiffSelectionModel();
        selection.Begin(null, At(0, 3));
        Assert.False(selection.HasRange);
        Assert.False(selection.TryRowSpan(null, 0, 8, out _));
    }

    // ---- scope isolation (the review list stacks many files on one surface) ----

    [Fact]
    public void SelectionIgnoresPositionsFromAnotherScope()
    {
        var selection = new DiffSelectionModel();
        selection.Begin("a.cs", At(0, 0));
        Assert.False(selection.ExtendTo("b.cs", At(5, 0)));
        Assert.Equal(At(0, 0), selection.Focus);
        Assert.True(selection.ExtendTo("a.cs", At(2, 0)));
        Assert.Equal(At(2, 0), selection.Focus);
    }

    [Fact]
    public void RowSpanRejectsAnotherScopesRows()
    {
        var selection = new DiffSelectionModel();
        selection.SetRange("a.cs", At(0, 0), At(0, 4));
        Assert.False(selection.TryRowSpan("b.cs", 0, 4, out _));
    }

    // ---- anchor / focus ordering ----

    [Fact]
    public void DraggingUpwardsNormalizesStartAndEnd()
    {
        var selection = new DiffSelectionModel();
        selection.Begin(null, At(4, 2));
        selection.ExtendTo(null, At(1, 6));
        Assert.Equal(At(1, 6), selection.Start);
        Assert.Equal(At(4, 2), selection.End);
    }

    [Fact]
    public void ClearDropsTheSelectionOnce()
    {
        var selection = new DiffSelectionModel();
        selection.Begin(null, At(0, 0));
        Assert.True(selection.Clear());
        Assert.False(selection.Clear());
        Assert.False(selection.IsActive);
    }

    // ---- word / line spans ----

    [Theory]
    [InlineData(0, 0, 3)]   // inside "var"
    [InlineData(2, 0, 3)]   // at the word's trailing edge
    [InlineData(4, 4, 9)]   // inside "total"
    public void WordSpanGrowsToWordBounds(int at, int expectedFrom, int expectedTo)
    {
        var rows = new List<DiffRow> { Line("var total = 1;") };
        var (start, end) = DiffSelectionModel.WordSpan(rows, At(0, at));
        Assert.Equal(At(0, expectedFrom), start);
        Assert.Equal(At(0, expectedTo), end);
    }

    [Fact]
    public void WordSpanOnWhitespaceTakesTheWhitespaceRun()
    {
        var rows = new List<DiffRow> { Line("    x") };
        var (start, end) = DiffSelectionModel.WordSpan(rows, At(0, 1));
        Assert.Equal(At(0, 0), start);
        Assert.Equal(At(0, 4), end);
    }

    [Fact]
    public void WordSpanOnAnEmptyLineCollapses()
    {
        var rows = new List<DiffRow> { Line("") };
        var (start, end) = DiffSelectionModel.WordSpan(rows, At(0, 0));
        Assert.Equal(start, end);
    }

    [Fact]
    public void LineSpanTakesTheWholeLine()
    {
        var rows = new List<DiffRow> { Line("hello") };
        var (start, end) = DiffSelectionModel.LineSpan(rows, At(0, 3));
        Assert.Equal(At(0, 0), start);
        Assert.Equal(At(0, 5), end);
    }

    [Fact]
    public void WholeSpanRunsToTheEndOfTheLastLine()
    {
        var rows = new List<DiffRow> { Line("a"), Bar(), Line("bcd") };
        var span = DiffSelectionModel.WholeSpan(rows);
        Assert.NotNull(span);
        Assert.Equal(At(0, 0), span!.Value.Start);
        Assert.Equal(At(2, 3), span.Value.End);
    }

    [Fact]
    public void WholeSpanOfAnEmptyRowSetIsNull()
    {
        Assert.Null(DiffSelectionModel.WholeSpan(Array.Empty<DiffRow>()));
    }
}
