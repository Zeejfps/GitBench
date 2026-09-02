using Xunit;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// The conversion between what a language server means by a position and where the preview draws
/// it. Rows are not lines: the list contains headers and separators, a collapsed fold's body has
/// no row at all, and tabs are spaces by the time they are painted. Every failure in this file is
/// a jump or an underline landing somewhere the user did not ask for, which is the one failure in
/// this feature that looks like it worked.
/// </summary>
public sealed class PositionMappingTests
{
    private static readonly DocumentRow Chrome = DocumentRow.Chrome;

    private static DocumentRow Row(int line) => DocumentRow.For(new LspLine(line));

    private static RenderedDocument Doc(string text, params DocumentRow[] rows) =>
        RenderedDocument.Of(FileText.Of(text), rows);

    private static LspPosition At(int line, int character) => new(new LspLine(line), new LspCharacter(character));

    private static ScreenPosition On(int row, int column) => new(new RowIndex(row), new ScreenColumn(column));

    private static ScreenSpan Span(int row, int from, int to) =>
        new(new RowIndex(row), new ScreenColumn(from), new ScreenColumn(to));

    // The whole point of the two types: a position we send has to come back to the same place.
    [Theory]
    [InlineData("var total = Compute();")]
    [InlineData("\tif err != nil {")]
    [InlineData("\t\treturn 1")]
    [InlineData("  \tmixed indent")]
    [InlineData("a\tb\tc")]
    [InlineData("let party = \"🎉\";")]
    [InlineData("const 名前 = \"日本語\";")]
    [InlineData("\t")]
    [InlineData("")]
    public void EveryFileCharacterSurvivesTheTripToScreenAndBack(string line)
    {
        // A header above the code, as a real preview has, so a row index that happens to equal a
        // line number cannot carry the test.
        var doc = Doc(line, Chrome, Row(0));

        for (var i = 0; i <= line.Length; i++)
        {
            var position = At(0, i);
            var shown = Assert.IsType<ScreenLookup.Shown>(doc.ToScreen(position));
            Assert.Equal(new FileLookup.At(position), doc.ToFile(shown.Position));
        }
    }

    // A tab is one character. There is no offset between its spaces to land on, so a screen column
    // that fell inside them belongs to the tab, and converting back lands on the tab's first
    // column rather than where the click was.
    [Fact]
    public void AScreenColumnInsideATabResolvesToTheTabItself()
    {
        var doc = Doc("\tvalue", Row(0));

        Assert.Equal(new FileLookup.At(At(0, 0)), doc.ToFile(On(0, 2)));
        Assert.Equal(new FileLookup.At(At(0, 1)), doc.ToFile(On(0, LineText.TabWidth)));
        Assert.Equal(new ScreenLookup.Shown(On(0, 0)), doc.ToScreen(At(0, 0)));
    }

    // The failure this whole file exists to catch: headers above and a collapsed fold in the
    // middle push the two numbers apart, and passing one where the other is meant jumps to the
    // wrong place while looking exactly like a working feature.
    [Fact]
    public void ARowIndexIsNotALineNumber()
    {
        var doc = Doc("0\n1\n2\n3\n4", Chrome, Row(0), Row(1), Row(4));

        Assert.Equal(new ScreenLookup.Shown(On(3, 0)), doc.ToScreen(At(4, 0)));
        Assert.Equal(new FileLookup.At(At(4, 0)), doc.ToFile(On(3, 0)));
    }

    [Fact]
    public void ARowThatDrawsNoFileTextHasNoFilePosition()
    {
        var doc = Doc("first\nsecond", Chrome, Row(0), Chrome, Row(1));

        Assert.IsType<FileLookup.NoLine>(doc.ToFile(On(0, 0)));
        Assert.IsType<FileLookup.NoLine>(doc.ToFile(On(2, 3)));
        Assert.Equal(new FileLookup.At(At(1, 0)), doc.ToFile(On(3, 0)));
    }

    [Fact]
    public void ARowOutsideTheDocumentHasNoFilePosition()
    {
        var doc = Doc("first\nsecond", Row(0), Row(1));

        Assert.IsType<FileLookup.NoLine>(doc.ToFile(On(-1, 0)));
        Assert.IsType<FileLookup.NoLine>(doc.ToFile(On(2, 0)));
    }

    // Inside a collapsed fold there is no row to point at, but there is somewhere to scroll to.
    [Fact]
    public void ALineInsideACollapsedFoldIsHiddenBehindTheRowAboveIt()
    {
        var doc = Doc("0\n1\n2\n3\n4\n5", Row(0), Row(1), Row(5));

        var hidden = Assert.IsType<ScreenLookup.Hidden>(doc.ToScreen(At(3, 0)));
        Assert.Equal(new RowIndex(1), hidden.Anchor);
    }

    [Fact]
    public void ALineHiddenAboveEverythingDrawnAnchorsToTheFirstRow()
    {
        var doc = Doc("0\n1\n2\n3", Chrome, Row(2), Row(3));

        var hidden = Assert.IsType<ScreenLookup.Hidden>(doc.ToScreen(At(0, 0)));
        Assert.Equal(new RowIndex(1), hidden.Anchor);
    }

    // A result that outlived the text it was computed against names a line that is not there. That
    // is a different answer from "hidden", because there is nothing to scroll to and nothing to
    // unfold — the result has to be thrown away.
    [Fact]
    public void ALineTheFileDoesNotHaveIsOffTheDocument()
    {
        var doc = Doc("0\n1\n2", Row(0), Row(1), Row(2));

        Assert.IsType<ScreenLookup.OffDocument>(doc.ToScreen(At(3, 0)));
    }

    // Below the document there is nothing to be off the end of: the protocol has no negative
    // position, so one cannot be built to ask about.
    [Fact]
    public void APositionBeforeTheStartOfTheFileCannotBeBuiltAtAll()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LspLine(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LspCharacter(-1));
    }

    [Fact]
    public void ACharacterPastTheEndOfItsLineClampsToTheEndOfTheLine()
    {
        var doc = Doc("abc", Row(0));

        Assert.Equal(new ScreenLookup.Shown(On(0, 3)), doc.ToScreen(At(0, 999)));
        Assert.Equal(new FileLookup.At(At(0, 3)), doc.ToFile(On(0, 999)));
    }

    // The terminator is not part of the line, so the same file checked out with either ending
    // gives a server the same offsets — and a diagnostic on the last column lands identically.
    [Fact]
    public void WindowsLineEndingsDoNotShiftAnyOffset()
    {
        const string body = "fn main() {\n\tprintln!();\n}";
        var lf = Doc(body, Row(0), Row(1), Row(2));
        var crlf = Doc(body.Replace("\n", "\r\n"), Row(0), Row(1), Row(2));

        Assert.Equal(lf.LineCount, crlf.LineCount);
        Assert.Equal(lf.ToScreen(At(1, 12)), crlf.ToScreen(At(1, 12)));
        Assert.Equal(lf.ToFile(On(1, 999)), crlf.ToFile(On(1, 999)));
    }

    [Fact]
    public void ATrailingNewlineDoesNotAddAnEmptyLastLine()
    {
        var doc = Doc("a\nb\n", Row(0), Row(1));

        Assert.Equal(2, doc.LineCount);
        Assert.IsType<ScreenLookup.OffDocument>(doc.ToScreen(At(2, 0)));
    }

    [Fact]
    public void AnEmptyFileIsOneEmptyLine()
    {
        var doc = Doc("", Row(0));

        Assert.Equal(1, doc.LineCount);
        Assert.Equal(new ScreenLookup.Shown(On(0, 0)), doc.ToScreen(At(0, 0)));
        Assert.Equal(new ScreenLookup.Shown(On(0, 0)), doc.ToScreen(At(0, 7)));
    }

    // Offsets are UTF-16 code units on both sides, so a surrogate pair shifts both spaces by two.
    [Fact]
    public void AnAstralCharacterIsTwoOffsetsInBothSpaces()
    {
        var doc = Doc("\t🎉 = 1", Row(0));

        Assert.Equal(new ScreenLookup.Shown(On(0, LineText.TabWidth + 2)), doc.ToScreen(At(0, 3)));
        Assert.Equal(new FileLookup.At(At(0, 3)), doc.ToFile(On(0, LineText.TabWidth + 2)));
    }

    // A wide glyph is still one code unit: only tabs change width between the two spaces.
    [Fact]
    public void ACjkCharacterIsOneOffsetInBothSpaces()
    {
        var doc = Doc("\tconst 名前 = 1;", Row(0));

        Assert.Equal(new ScreenLookup.Shown(On(0, LineText.TabWidth + 8)), doc.ToScreen(At(0, 9)));
        Assert.Equal(new FileLookup.At(At(0, 9)), doc.ToFile(On(0, LineText.TabWidth + 8)));
    }

    [Fact]
    public void RowsNamingALineTheFileDoesNotHaveAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Doc("a\nb", Row(0), Row(2)));
    }

    [Fact]
    public void ADocumentThatDrawsNoFileLineIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Doc("a\nb", Chrome, Chrome));
    }

    [Fact]
    public void ARangeOnOneLineUnderlinesJustThatSpan()
    {
        var doc = Doc("let total = 1;\nlet x = 2;", Row(0), Row(1));

        Assert.Equal(new[] { Span(0, 4, 9) }, doc.ToScreenSpans(new LspRange(At(0, 4), At(0, 9))));
    }

    [Fact]
    public void ARangeAcrossLinesUnderlinesEachRowToItsOwnEnd()
    {
        var doc = Doc("fn a() {\n\tlet x = 1;\n}", Row(0), Row(1), Row(2));

        Assert.Equal(
            new[] { Span(0, 3, 8), Span(1, 0, LineText.TabWidth + 10), Span(2, 0, 1) },
            doc.ToScreenSpans(new LspRange(At(0, 3), At(2, 1))));
    }

    [Fact]
    public void ARangeReachingIntoACollapsedFoldUnderlinesOnlyWhatIsDrawn()
    {
        var doc = Doc("0\n1\n2\n3\n4", Row(0), Row(1), Row(4));

        Assert.Equal(new[] { Span(1, 0, 1), Span(2, 0, 1) }, doc.ToScreenSpans(new LspRange(At(1, 0), At(4, 1))));
    }

    [Fact]
    public void ARangeEntirelyInsideACollapsedFoldUnderlinesNothing()
    {
        var doc = Doc("0\n1\n2\n3\n4", Row(0), Row(1), Row(4));

        Assert.Empty(doc.ToScreenSpans(new LspRange(At(2, 0), At(3, 1))));
    }

    // Servers do send empty ranges. There is still a place to mark, and dropping it would hide the
    // diagnostic entirely.
    [Fact]
    public void AnEmptyRangeStillMarksItsRow()
    {
        var doc = Doc("let x = 1;", Row(0));

        var span = Assert.Single(doc.ToScreenSpans(new LspRange(At(0, 4), At(0, 4))));
        Assert.Equal(Span(0, 4, 4), span);
    }

    // A range ending at column 0 of the next line covers the newline before it, not that line.
    [Fact]
    public void ARangeEndingAtTheStartOfTheNextLineDoesNotMarkThatLine()
    {
        var doc = Doc("a\nb\nc", Row(0), Row(1), Row(2));

        Assert.Equal(new[] { Span(0, 0, 1) }, doc.ToScreenSpans(new LspRange(At(0, 0), At(1, 0))));
    }
}
