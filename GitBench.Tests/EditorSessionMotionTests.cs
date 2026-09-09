using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>Caret motion, the goal column a vertical run aims for, and which end of a live selection an arrow collapses to.</summary>
public sealed class EditorSessionMotionTests
{
    [Fact]
    public void LeftWithALiveSelectionCollapsesToItsStart()
    {
        var session = EditorSession.Of("hello");

        var after = session.MoveBy(EditorSession.Select(1, 1, 1, 4), TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move);

        Assert.True(after.IsEmpty);
        Assert.Equal(TextPosition.At(1, 1), after.Caret);
    }

    [Fact]
    public void RightWithALiveSelectionCollapsesToItsEnd()
    {
        var session = EditorSession.Of("hello");

        var after = session.MoveBy(EditorSession.Select(1, 1, 1, 4), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move);

        Assert.True(after.IsEmpty);
        Assert.Equal(TextPosition.At(1, 4), after.Caret);
    }

    [Fact]
    public void CollapsingReadsTheSelectionsEndsRatherThanTheCaretsSide()
    {
        var session = EditorSession.Of("hello");

        var backward = EditorSession.Select(1, 4, 1, 1);

        Assert.Equal(
            TextPosition.At(1, 1),
            session.MoveBy(backward, TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 4),
            session.MoveBy(backward, TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void ExtendingLeftShrinksASelectionBuiltRightwards()
    {
        var session = EditorSession.Of("hello");

        var after = session.MoveBy(EditorSession.Select(1, 1, 1, 4), TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Extend);

        Assert.Equal(TextPosition.At(1, 1), after.Anchor);
        Assert.Equal(TextPosition.At(1, 3), after.Caret);
    }

    [Fact]
    public void ClusterMotionCrossesLineBreaks()
    {
        var session = EditorSession.Of("ab\ncd");

        Assert.Equal(
            TextPosition.At(2, 0),
            session.MoveBy(EditorSession.Caret(1, 2), TextUnit.Cluster, MoveDirection.Forward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 2),
            session.MoveBy(EditorSession.Caret(2, 0), TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void WordMotionCrossesLineBreaksOnlyWhenThereIsNoWordLeft()
    {
        var session = EditorSession.Of("one two\nthree");

        Assert.Equal(
            TextPosition.At(1, 4),
            session.MoveBy(EditorSession.Caret(1, 0), TextUnit.Word, MoveDirection.Forward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(2, 0),
            session.MoveBy(EditorSession.Caret(1, 7), TextUnit.Word, MoveDirection.Forward, SelectionIntent.Move).Caret);
        Assert.Equal(
            TextPosition.At(1, 7),
            session.MoveBy(EditorSession.Caret(2, 0), TextUnit.Word, MoveDirection.Backward, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void WordMotionMovesFromTheCaretEvenWithALiveSelection()
    {
        var session = EditorSession.Of("one two three");

        var after = session.MoveBy(EditorSession.Select(1, 8, 1, 4), TextUnit.Word, MoveDirection.Backward, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 0), after.Caret);
    }

    [Fact]
    public void HomeStopsAtTheCodeBeforeItStopsAtTheMargin()
    {
        var session = EditorSession.Of("    foo");

        var first = session.MoveToLineEdge(EditorSession.Caret(1, 6), LineEdge.SmartStart, SelectionIntent.Move);
        var second = session.MoveToLineEdge(first, LineEdge.SmartStart, SelectionIntent.Move);
        var third = session.MoveToLineEdge(second, LineEdge.SmartStart, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 4), first.Caret);
        Assert.Equal(TextPosition.At(1, 0), second.Caret);
        Assert.Equal(TextPosition.At(1, 4), third.Caret);
    }

    [Fact]
    public void HomeOnALineOfOnlyWhitespaceStillHasTwoStops()
    {
        var session = EditorSession.Of("    ");

        var first = session.MoveToLineEdge(EditorSession.Caret(1, 4), LineEdge.SmartStart, SelectionIntent.Move);
        var second = session.MoveToLineEdge(first, LineEdge.SmartStart, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(1, 0), first.Caret);
        Assert.Equal(TextPosition.At(1, 4), second.Caret);
    }

    [Fact]
    public void EndStopsPastTheLastCharacterOfTheLine()
    {
        var session = EditorSession.Of("ab\ncdef");

        Assert.Equal(
            TextPosition.At(2, 4),
            session.MoveToLineEdge(EditorSession.Caret(2, 1), LineEdge.End, SelectionIntent.Move).Caret);
    }

    [Fact]
    public void TheGoalCellSurvivesAPassThroughAShortLine()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        var caret = EditorSession.Caret(1, 6);
        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(2, 2), caret.Caret);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(3, 6), caret.Caret);
        Assert.Equal(new CellColumn(6), session.GoalCell);
    }

    [Fact]
    public void AHorizontalMoveClearsTheGoalCell()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        var caret = session.MoveByLine(EditorSession.Caret(1, 6), 1, SelectionIntent.Move);
        caret = session.MoveBy(caret, TextUnit.Cluster, MoveDirection.Backward, SelectionIntent.Move);
        Assert.Null(session.GoalCell);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);

        Assert.Equal(TextPosition.At(3, 1), caret.Caret);
        Assert.Equal(new CellColumn(1), session.GoalCell);
    }

    [Fact]
    public void EveryOtherMotionClearsTheGoalCell()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        var caret = session.MoveByLine(EditorSession.Caret(1, 6), 1, SelectionIntent.Move);
        Assert.NotNull(session.GoalCell);

        session.MoveToLineEdge(caret, LineEdge.End, SelectionIntent.Move);
        Assert.Null(session.GoalCell);

        session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.NotNull(session.GoalCell);

        session.MoveToDocumentEdge(caret, DocumentEdge.Start, SelectionIntent.Move);
        Assert.Null(session.GoalCell);

        session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.NotNull(session.GoalCell);

        session.MoveBy(caret, TextUnit.Word, MoveDirection.Forward, SelectionIntent.Move);
        Assert.Null(session.GoalCell);
    }

    [Fact]
    public void AnEditClearsTheGoalCell()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        var caret = session.MoveByLine(EditorSession.Caret(1, 6), 1, SelectionIntent.Move);
        Assert.NotNull(session.GoalCell);

        session.Type(caret, "x");

        Assert.Null(session.GoalCell);
    }

    [Fact]
    public void AnEditThatChangesNothingStillEndsTheVerticalRun()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        var caret = session.MoveByLine(EditorSession.Caret(1, 6), 1, SelectionIntent.Move);
        session.Delete(EditorSession.Caret(1, 0), TextUnit.Cluster, MoveDirection.Backward);

        Assert.Null(session.GoalCell);
        Assert.Equal(TextPosition.At(2, 2), caret.Caret);
    }

    [Fact]
    public void UndoEndsTheVerticalRun()
    {
        var session = EditorSession.Of("aaaaaaaa\nbb\ncccccccc");

        session.Type(EditorSession.Caret(1, 0), "x");
        session.MoveByLine(EditorSession.Caret(1, 6), 1, SelectionIntent.Move);
        Assert.NotNull(session.GoalCell);

        session.Undo();

        Assert.Null(session.GoalCell);
    }

    [Fact]
    public void ExtendingVerticallyKeepsTheAnchorWhereItWasPut()
    {
        var session = EditorSession.Of("aaaa\nbbbb\ncccc");

        var selection = session.MoveByLine(EditorSession.Caret(1, 2), 1, SelectionIntent.Extend);
        selection = session.MoveByLine(selection, 1, SelectionIntent.Extend);

        Assert.Equal(TextPosition.At(1, 2), selection.Anchor);
        Assert.Equal(TextPosition.At(3, 2), selection.Caret);
    }

    [Fact]
    public void TheGoalCellCrossesALineThatHasBothATabAndACjkGlyph()
    {
        var session = EditorSession.Of("\t日x\n日日日x\n\tyyyy");

        var caret = session.MoveByLine(EditorSession.Caret(1, 2), 1, SelectionIntent.Move);
        Assert.Equal(new CellColumn(6), session.GoalCell);
        Assert.Equal(TextPosition.At(2, 3), caret.Caret);

        caret = session.MoveByLine(caret, 1, SelectionIntent.Move);
        Assert.Equal(TextPosition.At(3, 3), caret.Caret);
    }

    [Fact]
    public void VerticalMotionLandsOnTheNearestCellOnEveryLineOfAMixedCorpus()
    {
        string[] lines =
        [
            "abcdefghij",
            "\tif (x)",
            "\t\u65e5\u672c\u8a9ex",
            "\u65e5\t\u672c\tx",
            "e\u0301\tcafe\u0301",
            "\t\ud840\udc0dz",
            "e\u0301\tcombining",
            "\te\u0301x",
            "        deeply",
            "",
        ];

        var session = EditorSession.Of(string.Join('\n', lines));

        for (var from = 0; from < lines.Length; from++)
        for (var to = 0; to < lines.Length; to++)
        {
            if (from == to) continue;
            for (var column = 0; column <= lines[from].Length; column++)
            {
                session.ClearGoal();
                var start = EditorSession.Caret(from + 1, column);
                var goal = session.CellAt(start.Caret).Value;
                var landed = session.MoveByLine(start, to - from, SelectionIntent.Move).Caret;

                Assert.Equal(to + 1, landed.Line.Value);
                Assert.Equal(NearestColumnToCell(lines[to], goal), landed.Column.Value);
            }
        }
    }

    private static int NearestColumnToCell(string text, int cell)
    {
        if (cell <= 0) return 0;

        var cells = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var step = char.IsSurrogatePair(text, i) ? 2 : 1;
            var width = text[i] == '\t'
                ? DiffOptions.TabWidth
                : DiffText.CellsBefore(text, i + step) - DiffText.CellsBefore(text, i);
            i += step;
            if (width == 0) continue;
            if (cell < cells + width / 2f) return start;
            cells += width;
        }
        return text.Length;
    }
}
