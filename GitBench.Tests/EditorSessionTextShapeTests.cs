using GitBench.Infrastructure;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using ZGF.Gui;
using Xunit;

namespace GitBench.Tests;

/// <summary>The editing vocabulary over the three shapes that break an editor written against ASCII: CRLF, tabs and CJK.</summary>
public sealed class EditorSessionTextShapeTests
{
    private const string Han = "漢字";
    private const string TrebleClef = "𝄞";

    private static EditSession Crlf(string text, string? lineComment = null) =>
        EditorSession.Of(text, EditorSession.Options(eol: LineEnding.CrLf, lineComment: lineComment));

    private static EditSession Tabbed(string text, string? lineComment = null) =>
        EditorSession.Of(text, EditorSession.Options(indent: IndentStyle.Tabs, lineComment: lineComment));

    [Fact]
    public void TypingIntoACrlfDocumentLeavesItsLineBreaksAlone()
    {
        var session = Crlf("a\r\nbb\r\nccc");

        var after = session.Type(EditorSession.Caret(2, 2), "X");

        Assert.Equal("a\r\nbbX\r\nccc", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 3), after.Caret);
    }

    [Fact]
    public void BackspaceAtTheStartOfACrlfLineTakesBothCharacters()
    {
        var session = Crlf("a\r\nbb\r\nccc");

        var after = session.Delete(EditorSession.Caret(2, 0), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("abb\r\nccc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
        Assert.Equal(2, session.Document.LineCount);
    }

    [Fact]
    public void ForwardDeleteAtTheEndOfACrlfLineTakesBothCharacters()
    {
        var session = Crlf("a\r\nbb\r\nccc");

        var after = session.Delete(EditorSession.Caret(1, 1), TextUnit.Cluster, MoveDirection.Forward);

        Assert.Equal("abb\r\nccc", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void NewlineInACrlfDocumentIsACrlf()
    {
        var session = Crlf("    a\r\nb");

        var after = session.InsertNewline(EditorSession.Caret(1, 5));

        Assert.Equal("    a\r\n    \r\nb", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 4), after.Caret);
    }

    [Fact]
    public void PastedTextTakesTheDocumentsLineEndings()
    {
        var session = Crlf("a\r\nb");

        session.Paste(EditorSession.Caret(1, 1), "\nx\ry\r\nz");

        Assert.Equal("a\r\nx\r\ny\r\nz\r\nb", session.Document.Text);
        Assert.Equal(5, session.Document.LineCount);
    }

    [Fact]
    public void IndentAndOutdentOverACrlfBlockLeaveItsLineBreaksAlone()
    {
        var session = Crlf("a\r\nb\r\nc");

        var selection = session.Indent(EditorSession.Select(1, 0, 3, 1));
        Assert.Equal("    a\r\n    b\r\n    c", session.Document.Text);

        session.Outdent(selection);
        Assert.Equal("a\r\nb\r\nc", session.Document.Text);
    }

    [Fact]
    public void ToggleCommentOverACrlfBlockLeavesItsLineBreaksAlone()
    {
        var session = Crlf("a\r\nb", "//");

        var selection = session.ToggleLineComment(EditorSession.Select(1, 0, 2, 1));
        Assert.Equal("// a\r\n// b", session.Document.Text);

        session.ToggleLineComment(selection);
        Assert.Equal("a\r\nb", session.Document.Text);
    }

    [Fact]
    public void MotionOverACrlfLineBreakIsOneStep()
    {
        var session = Crlf("ab\r\ncd");

        Assert.Equal(
            TextPosition.At(2, 0),
            session.MoveBy(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 2),
            session.MoveBy(EditorSession.Caret(2, 0), TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(2, 1),
            session.MoveByLine(EditorSession.Caret(1, 1), 1, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void SelectingAcrossACrlfBreakAndTypingReplacesTheWholeSpan()
    {
        var session = Crlf("ab\r\ncd");

        var after = session.Type(EditorSession.Select(1, 1, 2, 1), "X");

        Assert.Equal("aXd", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 2), after.Caret);
        Assert.Equal(1, session.Document.LineCount);
    }

    [Fact]
    public void ACaretAndAnAnchorOnTheSameCharacterAgreeAcrossAWidenedEdit()
    {
        var session = EditorSession.Of("a\rX\nb");

        var after = session.Delete(EditorSession.Caret(2, 0), TextUnit.Cluster, MoveDirection.Forward);

        Assert.Equal("a\r\nb", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void ANewlineTypedAgainstALoneCrLeavesTheCaretOnTheLineItOpened()
    {
        var session = EditorSession.Of("a\rb");

        var after = session.Type(EditorSession.Caret(2, 0), "\n");

        Assert.Equal("a\r\nb", session.Document.Text);
        Assert.Equal(2, session.Document.LineCount);
        Assert.Equal(TextPosition.At(2, 0), after.Caret);
    }

    [Fact]
    public void ACellColumnCountsATabAsTheWidthItIsDrawn()
    {
        var session = Tabbed("\t\tfoo");

        Assert.Equal(new CellColumn(0), session.CellAt(TextPosition.At(1, 0)));
        Assert.Equal(new CellColumn(4), session.CellAt(TextPosition.At(1, 1)));
        Assert.Equal(new CellColumn(8), session.CellAt(TextPosition.At(1, 2)));
        Assert.Equal(new CellColumn(11), session.CellAt(TextPosition.At(1, 5)));
    }

    [Fact]
    public void ACaretNeverLandsBetweenTheSpacesATabExpandsInto()
    {
        var session = Tabbed("\t\tfoo");
        int[] stops = [0, 4, 8, 9, 10, 11];

        for (var cell = 0; cell <= 11; cell++)
        {
            var landed = session.CellAt(session.PositionAtCell(new FileLine(1), new CellColumn(cell))).Value;
            Assert.Contains(landed, stops);
        }
    }

    [Fact]
    public void TheGoalCellOverTabbedLinesIsMeasuredInCells()
    {
        var session = Tabbed("\t\tfoo\nx\n\t\tbar");

        var caret = session.MoveByLine(EditorSession.Caret(1, 5), 1, SelectionIntent.Move);
        Assert.Equal(new CellColumn(11), session.GoalCell);
        Assert.Equal(TextPosition.At(2, 1), caret.Caret);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(3, 5), caret.Caret);
    }

    [Fact]
    public void HomeOnATabbedLineStopsAtTheFirstNonWhitespaceCharacter()
    {
        var session = Tabbed("\t\tfoo");

        var first = session.MoveToLineEdge(EditorSession.Caret(1, 5), LineEdge.SmartStart, SelectionIntent.Move);
        var second = session.MoveToLineEdge(first, LineEdge.SmartStart, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 2), first.Caret);
        Assert.Equal(TextPosition.At(1, 0), second.Caret);
    }

    [Fact]
    public void OutdentTakesOneTabAtATime()
    {
        var session = Tabbed("\t\tfoo\n\tbar");

        session.Outdent(EditorSession.Select(1, 0, 2, 1));

        Assert.Equal("\tfoo\nbar", session.Document.Text);
    }

    [Fact]
    public void NewlineCopiesATabbedIndent()
    {
        var session = Tabbed("\t\tfoo");

        var after = session.InsertNewline(EditorSession.Caret(1, 5));

        Assert.Equal("\t\tfoo\n\t\t", session.Document.Text);
        Assert.Equal(TextPosition.At(2, 2), after.Caret);
    }

    [Fact]
    public void ToggleCommentOnATabbedLineInsertsAfterTheTabs()
    {
        var session = Tabbed("\t\tfoo", "//");

        session.ToggleLineComment(EditorSession.Caret(1, 5));

        Assert.Equal("\t\t// foo", session.Document.Text);
    }

    [Fact]
    public void ClusterMotionStepsOverATabAsOneCharacter()
    {
        var session = Tabbed("\tfoo");

        var after = session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 1), after.Caret);
        Assert.Equal(new CellColumn(4), session.CellAt(after.Caret));
    }

    [Fact]
    public void BackspaceOverATabTakesTheWholeTab()
    {
        var session = Tabbed("\t\tfoo");

        session.Delete(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("\tfoo", session.Document.Text);
    }

    [Fact]
    public void IndentAtACaretRunsToTheNextStopEvenPastTabs()
    {
        var session = EditorSession.Of("\tab");

        var after = session.Indent(EditorSession.Caret(1, 3));

        Assert.Equal("\tab  ", session.Document.Text);
        Assert.Equal(TextPosition.At(1, 5), after.Caret);
    }

    [Fact]
    public void ACellColumnCountsAWideGlyphAsTwo()
    {
        var session = EditorSession.Of(Han + "abc");

        Assert.Equal(new CellColumn(2), session.CellAt(TextPosition.At(1, 1)));
        Assert.Equal(new CellColumn(4), session.CellAt(TextPosition.At(1, 2)));
        Assert.Equal(new CellColumn(7), session.CellAt(TextPosition.At(1, 5)));
    }

    [Fact]
    public void TheGoalCellOverWideGlyphsIsMeasuredInCells()
    {
        var session = EditorSession.Of(Han + "abc\nx\n" + Han + "abc");

        var caret = session.MoveByLine(EditorSession.Caret(1, 3), 1, SelectionIntent.Move);
        Assert.Equal(new CellColumn(5), session.GoalCell);
        Assert.Equal(TextPosition.At(2, 1), caret.Caret);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(3, 3), caret.Caret);
    }

    [Fact]
    public void ACaretNeverLandsInsideAWideGlyph()
    {
        var session = EditorSession.Of(Han + "abc");
        int[] stops = [0, 2, 4, 5, 6, 7];

        for (var cell = 0; cell <= 7; cell++)
        {
            var landed = session.CellAt(session.PositionAtCell(new FileLine(1), new CellColumn(cell))).Value;
            Assert.Contains(landed, stops);
        }
    }

    [Fact]
    public void ClusterMotionOverAWideGlyphIsOneStep()
    {
        var session = EditorSession.Of(Han + "abc");

        var after = session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void EachIdeographIsItsOwnWord()
    {
        var session = EditorSession.Of(Han + "abc");

        Assert.Equal(
            TextPosition.At(1, 1),
            session.MoveBy(EditorSession.Caret(1, 2), TextUnit.Word, MoveDirection.Backward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 1),
            session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Word, MoveDirection.Forward, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void BackspaceOverAWideGlyphTakesOneCharacter()
    {
        var session = EditorSession.Of(Han + "abc");

        session.Delete(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("漢abc", session.Document.Text);
    }

    [Fact]
    public void BackspaceOverASurrogatePairTakesBothHalves()
    {
        var session = EditorSession.Of(TrebleClef + "a");

        session.Delete(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Equal("a", session.Document.Text);
    }

    [Fact]
    public void TypingAnAstralRuneIsOneKeystrokeAndCoalesces()
    {
        var session = EditorSession.Of(string.Empty);

        var caret = session.Type(EditorSession.Caret(1, 0), TrebleClef);
        session.Type(caret, "a");

        Assert.Equal(TrebleClef + "a", session.Document.Text);
        Assert.Equal(1, session.Journal.UndoDepth);
    }

    [Fact]
    public void TheGoalCellOverALineWithBothATabAndAWideGlyphIsMeasuredInCells()
    {
        var session = EditorSession.Of("\t" + Han[0] + "x\ny\n\t" + Han[0] + "x");

        var caret = session.MoveByLine(EditorSession.Caret(1, 2), 1, SelectionIntent.Move);
        Assert.Equal(new CellColumn(6), session.GoalCell);
        Assert.Equal(TextPosition.At(2, 1), caret.Caret);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(3, 2), caret.Caret);
    }

    [Fact]
    public void ACaretNeverLandsInsideATabOnALineThatAlsoHasWideGlyphs()
    {
        var session = EditorSession.Of("\t" + Han[0] + "x");
        int[] stops = [0, 4, 6, 7];

        for (var cell = 0; cell <= 7; cell++)
        {
            var landed = session.CellAt(session.PositionAtCell(new FileLine(1), new CellColumn(cell))).Value;
            Assert.Contains(landed, stops);
        }
    }

    [Theory]
    [InlineData("\t\tfoo")]
    [InlineData("漢字abc")]
    [InlineData("\t漢x")]
    [InlineData("漢\t漢")]
    [InlineData("\t\t漢字\tx")]
    [InlineData("a\t漢\tb\t")]
    [InlineData("\téx")]
    [InlineData("\t𝄞漢")]
    public void EveryColumnOfALineSurvivesACellRoundTrip(string line)
    {
        var session = EditorSession.Of(line);

        for (var column = 0; column <= line.Length; column++)
        {
            if (TextBoundaries.Snap(line, column) != column) continue;
            var position = TextPosition.At(1, column);
            Assert.Equal(position, session.PositionAtCell(new FileLine(1), session.CellAt(position)));
        }
    }

    [Fact]
    public void IndentAndCommentOverWideGlyphLinesTouchOnlyTheLineStarts()
    {
        var session = EditorSession.Of(Han + "\n" + Han, EditorSession.Options(lineComment: "#"));

        var selection = EditorSession.Select(1, 0, 2, 2);
        selection = session.Indent(selection);
        Assert.Equal("    " + Han + "\n    " + Han, session.Document.Text);

        session.ToggleLineComment(selection);
        Assert.Equal("    # " + Han + "\n    # " + Han, session.Document.Text);
    }
}
