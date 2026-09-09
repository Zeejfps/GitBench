using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>Where an edit leaves a position that is not the one being edited.</summary>
public sealed class EditorPositionShiftTests
{
    private static TextEdit Edit(int startLine, int startColumn, int endLine, int endColumn, string replacement) =>
        new(new TextRange(TextPosition.At(startLine, startColumn), TextPosition.At(endLine, endColumn)), replacement);

    [Fact]
    public void APositionBeforeTheEditDoesNotMoveWhicheverBiasItHas()
    {
        var edit = Edit(2, 4, 2, 8, "replaced");

        foreach (var bias in new[] { AnchorBias.Before, AnchorBias.After })
        {
            Assert.Equal(TextPosition.At(2, 1), TextEdit.Shift(edit, TextPosition.At(2, 1), bias));
            Assert.Equal(TextPosition.At(1, 30), TextEdit.Shift(edit, TextPosition.At(1, 30), bias));
        }
    }

    [Fact]
    public void APositionAtTheEditStartStaysPutOrTravelsWithTheInsertion()
    {
        var edit = Edit(2, 4, 2, 4, "abc");
        var start = TextPosition.At(2, 4);

        Assert.Equal(start, TextEdit.Shift(edit, start, AnchorBias.Before));
        Assert.Equal(TextPosition.At(2, 7), TextEdit.Shift(edit, start, AnchorBias.After));
    }

    [Fact]
    public void APositionAtTheStartOfAMultiLineInsertionFollowsItToTheNewLine()
    {
        var edit = Edit(2, 4, 2, 4, "one\ntwo");
        var start = TextPosition.At(2, 4);

        Assert.Equal(start, TextEdit.Shift(edit, start, AnchorBias.Before));
        Assert.Equal(TextPosition.At(3, 3), TextEdit.Shift(edit, start, AnchorBias.After));
    }

    [Fact]
    public void APositionInsideTheReplacedRangeCollapsesToItsStart()
    {
        var edit = Edit(2, 4, 2, 9, "x");

        Assert.Equal(TextPosition.At(2, 4), TextEdit.Shift(edit, TextPosition.At(2, 6), AnchorBias.Before));
        Assert.Equal(TextPosition.At(2, 4), TextEdit.Shift(edit, TextPosition.At(2, 6), AnchorBias.After));
    }

    [Fact]
    public void APositionAtTheEditEndLandsPastTheReplacement()
    {
        var edit = Edit(2, 4, 2, 9, "xy");

        Assert.Equal(TextPosition.At(2, 6), TextEdit.Shift(edit, TextPosition.At(2, 9), AnchorBias.Before));
        Assert.Equal(TextPosition.At(2, 6), TextEdit.Shift(edit, TextPosition.At(2, 9), AnchorBias.After));
    }

    [Fact]
    public void APositionAfterTheEditOnItsLastLineKeepsItsDistanceFromTheEnd()
    {
        var edit = Edit(2, 4, 2, 9, "xy");

        Assert.Equal(TextPosition.At(2, 9), TextEdit.Shift(edit, TextPosition.At(2, 12), AnchorBias.Before));
    }

    [Fact]
    public void APositionOnALaterLineMovesByTheLinesTheEditAddedOrRemoved()
    {
        var added = Edit(2, 0, 2, 0, "one\ntwo\n");
        Assert.Equal(TextPosition.At(9, 5), TextEdit.Shift(added, TextPosition.At(7, 5), AnchorBias.Before));

        var removed = Edit(2, 0, 5, 0, string.Empty);
        Assert.Equal(TextPosition.At(4, 5), TextEdit.Shift(removed, TextPosition.At(7, 5), AnchorBias.Before));
    }

    [Fact]
    public void AnEditThatDeletesThePositionsLineEntirelyCollapsesItToTheEditStart()
    {
        var edit = Edit(3, 0, 6, 0, string.Empty);

        Assert.Equal(TextPosition.At(3, 0), TextEdit.Shift(edit, TextPosition.At(4, 7), AnchorBias.After));
        Assert.Equal(TextPosition.At(3, 0), TextEdit.Shift(edit, TextPosition.At(5, 0), AnchorBias.Before));
    }

    [Fact]
    public void AnEditEndingMidLineMovesTheRestOfThatLineOntoTheReplacementsLastLine()
    {
        var edit = Edit(2, 3, 4, 2, "a\nbb");

        Assert.Equal(TextPosition.At(3, 4), TextEdit.Shift(edit, TextPosition.At(4, 4), AnchorBias.Before));
        Assert.Equal(TextPosition.At(4, 1), TextEdit.Shift(edit, TextPosition.At(5, 1), AnchorBias.Before));
    }

    [Fact]
    public void ASelectionKeepsExactlyTheCharactersItHeldWhenTextIsInsertedAtEitherEdge()
    {
        var selection = new TextRange(TextPosition.At(1, 4), TextPosition.At(1, 9));

        var atTheStart = TextEdit.Shift(Edit(1, 4, 1, 4, "xx"), selection, AnchorBias.After);
        Assert.Equal(new TextRange(TextPosition.At(1, 6), TextPosition.At(1, 11)), atTheStart);

        var atTheEnd = TextEdit.Shift(Edit(1, 9, 1, 9, "xx"), selection, AnchorBias.After);
        Assert.Equal(selection, atTheEnd);
    }

    [Fact]
    public void ASelectionTheEditConsumesComesBackAsACaretRatherThanAnInvertedRange()
    {
        var selection = new TextRange(TextPosition.At(1, 2), TextPosition.At(1, 3));

        var shifted = TextEdit.Shift(Edit(1, 2, 1, 5, "XY"), selection, AnchorBias.After);

        Assert.True(shifted.IsEmpty);
        Assert.Equal(TextPosition.At(1, 4), shifted.Start);
    }

    [Fact]
    public void ACaretTakesTheBiasItWasGivenBecauseItHasNoInteriorToKeep()
    {
        var caret = TextRange.Caret(TextPosition.At(1, 4));
        var edit = Edit(1, 4, 1, 4, "abc");

        Assert.Equal(TextPosition.At(1, 7), TextEdit.Shift(edit, caret, AnchorBias.After).Start);
        Assert.Equal(TextPosition.At(1, 4), TextEdit.Shift(edit, caret, AnchorBias.Before).Start);
    }
}
