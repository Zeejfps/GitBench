using System.Text;
using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>What the editor's row projection has to do that a viewer's does not: follow an edit without rebuilding the file.</summary>
public sealed class EditorRowSetTests
{
    [Fact]
    public void AnEditLeavesEveryRowAboveItTheSameObject()
    {
        var (document, rows) = Document(Numbered(200));
        var before = Materialized(rows, 0, 200);

        rows.Reproject(document.Apply(Insert(40, 0, "x")));

        var after = Materialized(rows, 0, 200);
        for (var i = 0; i < 39; i++)
            Assert.Same(before[i], after[i]);
        Assert.NotSame(before[39], after[39]);
        for (var i = 40; i < 200; i++)
            Assert.Same(before[i], after[i]);
    }

    [Fact]
    public void AnEditMeasuresTheLineItLandedOnAndNoOther()
    {
        var (document, rows) = Document(Numbered(200));
        var measured = rows.LineMeasurements;

        rows.Reproject(document.Apply(Insert(40, 0, "x")));

        Assert.Equal(measured + 1, rows.LineMeasurements);
    }

    [Fact]
    public void SplittingALineMeasuresTheTwoLinesItMade()
    {
        var (document, rows) = Document(Numbered(200));
        var measured = rows.LineMeasurements;

        rows.Reproject(document.Apply(Insert(40, 2, "\n")));

        Assert.Equal(measured + 2, rows.LineMeasurements);
        Assert.Equal(201, rows.Rows.Count);
    }

    [Fact]
    public void RowsBelowAnInsertedLineRenumberThemselves()
    {
        var (document, rows) = Document(Numbered(200));
        Assert.Equal("line 41", Raw(rows, 40));

        rows.Reproject(document.Apply(Insert(40, 0, "new\n")));

        Assert.Equal("new", Raw(rows, 39));
        Assert.Equal("line 40", Raw(rows, 40));
        Assert.Equal(new FileLine(41), Line(rows, 40).NewNumber.Line!.Value);
        Assert.Equal("41", Line(rows, 40).NewNumber.Text);
    }

    [Fact]
    public void DeletingAcrossLinesDropsTheRowsItJoined()
    {
        var (document, rows) = Document(Numbered(20));

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(5, 4), TextPosition.At(8, 4)), string.Empty)));

        Assert.Equal(17, rows.Rows.Count);
        Assert.Equal("line 8", Raw(rows, 4));
        Assert.Equal("line 9", Raw(rows, 5));
    }

    [Fact]
    public void AProjectionThatMissedAnEditCatchesUpOnTheNextOne()
    {
        var (document, rows) = Document(Numbered(20));
        document.Apply(Insert(3, 0, "a"));

        rows.Reproject(document.Apply(Insert(4, 0, "b")));

        Assert.Equal(1, rows.Resynchronizations);
        Assert.Equal("aline 3", Raw(rows, 2));
        Assert.Equal("bline 4", Raw(rows, 3));
        Assert.Equal(20, rows.Rows.Count);
    }

    [Fact]
    public void HandedTheEditThatWasAppliedRatherThanItsInverseTheProjectionRebuilds()
    {
        var (document, rows) = Document(Numbered(20));
        var applied = new TextEdit(
            new TextRange(TextPosition.At(5, 0), TextPosition.At(9, 0)), "one\ntwo\n");
        document.Apply(applied);

        rows.Reproject(applied);

        Assert.Equal(1, rows.Resynchronizations);
        Assert.Equal(18, rows.Rows.Count);
        Assert.Equal("one", Raw(rows, 4));
        Assert.Equal("two", Raw(rows, 5));
        Assert.Equal("line 9", Raw(rows, 6));
    }

    [Fact]
    public void AnEditItCanFollowNeverCostsARebuild()
    {
        var (document, rows) = Document(Numbered(200));

        rows.Reproject(document.Apply(Insert(40, 0, "new\n")));
        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(10, 0), TextPosition.At(14, 0)), string.Empty)));

        Assert.Equal(0, rows.Resynchronizations);
    }

    [Fact]
    public void AnEditThatChangedNothingLeavesTheProjectionAlone()
    {
        var (document, rows) = Document(Numbered(20));
        var before = Materialized(rows, 0, 20);

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(5, 0)), string.Empty)));

        var after = Materialized(rows, 0, 20);
        for (var i = 0; i < before.Length; i++) Assert.Same(before[i], after[i]);
    }

    [Fact]
    public void DeletingTheLongestLineBringsTheWidthBackDown()
    {
        var (document, rows) = Document("short\n" + new string('x', 90) + "\nmid\n");
        Assert.Equal(90, rows.MaxRowCells);

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(2, 0), TextPosition.At(3, 0)), string.Empty)));

        Assert.Equal(5, rows.MaxRowCells);
    }

    [Fact]
    public void ShorteningTheLongestLineBringsTheWidthDownToTheNextOne()
    {
        var (document, rows) = Document("aaaaaaaaaa\nbbbb\n");
        Assert.Equal(10, rows.MaxRowCells);

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(1, 1), TextPosition.At(1, 10)), string.Empty)));

        Assert.Equal(4, rows.MaxRowCells);
    }

    [Fact]
    public void LengtheningALineRaisesTheWidth()
    {
        var (document, rows) = Document("aaaa\nbb\n");

        rows.Reproject(document.Apply(Insert(2, 2, new string('b', 20))));

        Assert.Equal(22, rows.MaxRowCells);
    }

    [Fact]
    public void TwoLinesAtTheSameWidthKeepItWhenOneOfThemGoes()
    {
        var (document, rows) = Document("aaaaa\naaaaa\nb\n");
        Assert.Equal(5, rows.MaxRowCells);

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(1, 0), TextPosition.At(2, 0)), string.Empty)));

        Assert.Equal(5, rows.MaxRowCells);
    }

    [Fact]
    public void ATabIsWorthItsExpansionAndACjkGlyphTwoCells()
    {
        var (_, rows) = Document("\t\tx\n你好\n");

        Assert.Equal(DiffText.VisualCells(DiffText.ExpandTabs("\t\tx")), rows.MaxRowCells);
        Assert.Equal("        x", Line(rows, 0).Text.Expanded);
        Assert.Equal("\t\tx", Line(rows, 0).Text.Raw);
        Assert.Equal(4, DiffText.VisualCells(Line(rows, 1).Text.Expanded));
    }

    [Fact]
    public void EditingATabbedLineRemeasuresItInCellsRatherThanCharacters()
    {
        var (document, rows) = Document("x\ny\n");

        rows.Reproject(document.Apply(Insert(1, 1, "\t")));

        Assert.Equal(DiffOptions.TabWidth + 1, rows.MaxRowCells);
    }

    [Fact]
    public void TheGutterWidensAsTheDocumentCrossesAThousandLines()
    {
        var (document, rows) = Document(Numbered(999));
        Assert.Equal(3, rows.GutterDigits);

        rows.Reproject(document.Apply(Insert(999, 8, "\nline 1000")));
        Assert.Equal(4, rows.GutterDigits);

        rows.Reproject(document.Apply(new TextEdit(
            new TextRange(TextPosition.At(999, 8), TextPosition.At(1000, 9)), string.Empty)));
        Assert.Equal(3, rows.GutterDigits);
    }

    [Fact]
    public void AnEmptyDocumentIsOneEmptyRow()
    {
        var (_, rows) = Document(string.Empty);

        Assert.Equal(1, rows.Rows.Count);
        Assert.Equal(string.Empty, Raw(rows, 0));
        Assert.Equal(0, rows.MaxRowCells);
        Assert.Equal(1, rows.GutterDigits);
        Assert.Equal(new RowIndex(0), rows.RowForNewLine(new FileLine(1)));
        Assert.Null(rows.RowForNewLine(new FileLine(2)));
    }

    [Fact]
    public void ADocumentThatIsOneNewlineIsTwoEmptyRows()
    {
        var (_, rows) = Document("\n");

        Assert.Equal(2, rows.Rows.Count);
        Assert.Equal(string.Empty, Raw(rows, 0));
        Assert.Equal(string.Empty, Raw(rows, 1));
        Assert.Equal(new FileLine(2), Line(rows, 1).NewNumber.Line!.Value);
    }

    [Fact]
    public void TypingIntoAnEmptyDocumentFillsItsOnlyRow()
    {
        var (document, rows) = Document(string.Empty);

        rows.Reproject(document.Apply(Insert(1, 0, "hi")));

        Assert.Equal(1, rows.Rows.Count);
        Assert.Equal("hi", Raw(rows, 0));
        Assert.Equal(2, rows.MaxRowCells);
    }

    [Fact]
    public void TheProjectionRendersASingleGutterAndReservesNoOtherColumn()
    {
        var (_, rows) = Document(Numbered(3));

        Assert.True(rows.SingleGutter);
        Assert.False(rows.FoldColumn);
        Assert.False(rows.GlyphColumn);
        Assert.All(rows.Rows, row =>
        {
            var line = Assert.IsType<DiffRow.Line>(row);
            Assert.Equal(DiffLineKind.Context, line.Kind);
            Assert.Equal(DiffGutterNumber.None, line.OldNumber);
            Assert.Null(line.Fold);
            Assert.Null(line.Emphasis);
        });
    }

    [Fact]
    public void ATruncatedDocumentClosesWithTheBannerTheViewerShows()
    {
        var loc = Loc();
        var document = TextDocument.FromText(Numbered(3));
        var rows = new EditorRowSet(document, loc, truncated: true);

        var banner = Assert.IsType<DiffRow.Banner>(rows.Rows[^1]);
        Assert.Equal(loc.Strings.Value.DiffFileTruncated(3), banner.Text);
        Assert.Equal(4, rows.Rows.Count);
        Assert.Equal(DiffText.VisualCells(banner.Text), rows.MaxRowCells);
    }

    [Fact]
    public void ALineOutsideTheDocumentHasNoRowButStillHasSomewhereToScroll()
    {
        var (_, rows) = Document(Numbered(10));

        Assert.Null(rows.RowForNewLine(new FileLine(11)));
        Assert.Equal(new RowIndex(9), rows.RowNearestNewLine(new FileLine(11)));
        Assert.Null(rows.RowNearestNewLine(new FileLine(0)));
        Assert.Equal(new FileLine(7), rows.NewLineAt(new RowIndex(6))!.Value);
        Assert.Null(rows.NewLineAt(new RowIndex(10)));
    }

    [Fact]
    public void AFreshHighlightRecolorsTheRowsWithoutMovingTheWidth()
    {
        var (document, rows) = Document("keyword\n");
        Assert.Null(Line(rows, 0).Spans);
        var width = rows.MaxRowCells;

        Assert.True(rows.SetAnnotations(Parsed(document, new DiffHighlight(null, new[]
        {
            (IReadOnlyList<TokenSpan>)new[] { new TokenSpan(0, 7, TokenColorSlot.Keyword) },
        }))));

        Assert.Equal(1, Line(rows, 0).Spans!.Count);
        Assert.Equal(width, rows.MaxRowCells);
    }

    private static (TextDocument Document, EditorRowSet Rows) Document(string text)
    {
        var document = TextDocument.FromText(text);
        return (document, new EditorRowSet(document, Loc()));
    }

    private static string Numbered(int lines)
    {
        var text = new StringBuilder();
        for (var i = 1; i <= lines; i++) text.Append("line ").Append(i).Append('\n');
        return text.ToString(0, text.Length - 1);
    }

    private static TextEdit Insert(int line, int column, string text) =>
        new(TextRange.Caret(TextPosition.At(line, column)), text);

    private static DiffRow.Line Line(EditorRowSet rows, int index) =>
        Assert.IsType<DiffRow.Line>(rows.Rows[index]);

    private static string Raw(EditorRowSet rows, int index) => Line(rows, index).Text.Raw;

    private static DiffRow[] Materialized(EditorRowSet rows, int from, int to)
    {
        var taken = new DiffRow[to - from];
        for (var i = from; i < to; i++) taken[i - from] = rows.Rows[i];
        return taken;
    }

    private static Revised<DiffAnnotations> Parsed(
        TextDocument document, DiffHighlight? highlight = null, FileOutline? outline = null) =>
        new(DocumentRevision.Of(document), new DiffAnnotations(highlight, outline, null));

    private static ILocalizationService Loc() => new LocalizationService(new State<Locale>(Locale.En));
}
