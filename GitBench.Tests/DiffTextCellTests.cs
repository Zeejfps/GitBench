using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

// The monospace cell grid is the contract between the hit-test (pointer x → character) and the
// painter (character → highlight x). If the two disagree, a selection highlights different text
// than the one the click aimed at — and the clipboard gets a third answer.
public class DiffTextCellTests
{
    [Theory]
    [InlineData("abc", 0, 0)]
    [InlineData("abc", 2, 2)]
    [InlineData("abc", 3, 3)]
    public void CellsBeforeCountsLatinOnePerChar(string text, int charIndex, int expected)
        => Assert.Equal(expected, DiffText.CellsBefore(text, charIndex));

    [Fact]
    public void CellsBeforeCountsWideGlyphsAsTwo()
    {
        // "日本" — two wide glyphs, four cells.
        Assert.Equal(0, DiffText.CellsBefore("日本語", 0));
        Assert.Equal(2, DiffText.CellsBefore("日本語", 1));
        Assert.Equal(4, DiffText.CellsBefore("日本語", 2));
    }

    [Fact]
    public void CellsBeforeClampsOutOfRangeOffsets()
    {
        Assert.Equal(0, DiffText.CellsBefore("abc", -5));
        Assert.Equal(3, DiffText.CellsBefore("abc", 99));
    }

    [Fact]
    public void CellsBeforeAgreesWithVisualCellsAtTheEnd()
    {
        const string text = "let 日 = 1;";
        Assert.Equal(DiffText.VisualCells(text), DiffText.CellsBefore(text, text.Length));
    }

    // A click snaps to the nearer character boundary, so the caret lands where the eye expects.
    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(0.4f, 0)]
    [InlineData(0.6f, 1)]
    [InlineData(1.4f, 1)]
    [InlineData(2.6f, 3)]
    [InlineData(99f, 3)]
    public void CharIndexAtCellSnapsToTheNearestBoundary(float cell, int expected)
        => Assert.Equal(expected, DiffText.CharIndexAtCell("abc", cell));

    // A two-cell glyph is never split: its midpoint is one whole cell in.
    [Theory]
    [InlineData(0.9f, 0)]
    [InlineData(1.1f, 1)]
    [InlineData(2.9f, 1)]
    [InlineData(3.1f, 2)]
    public void CharIndexAtCellNeverSplitsAWideGlyph(float cell, int expected)
        => Assert.Equal(expected, DiffText.CharIndexAtCell("日本", cell));

    // Surrogate pairs are one glyph, so a caret can't land between the two chars that encode it.
    [Fact]
    public void CharIndexAtCellNeverSplitsASurrogatePair()
    {
        const string text = "\U0001F600x"; // emoji (2 chars) + 'x'
        Assert.Equal(0, DiffText.CharIndexAtCell(text, 0.2f));
        Assert.Equal(2, DiffText.CharIndexAtCell(text, 1.2f));
        Assert.Equal(3, DiffText.CharIndexAtCell(text, 9f));
    }

    [Fact]
    public void CharIndexAtCellOnEmptyTextIsZero()
        => Assert.Equal(0, DiffText.CharIndexAtCell("", 4f));

    // Round trip: the cell a character starts at maps back to that character.
    [Theory]
    [InlineData("plain ascii", 6)]
    [InlineData("tabs\tgone", 5)]
    [InlineData("日本語text", 3)]
    public void CharIndexAtCellInvertsCellsBefore(string text, int charIndex)
    {
        var expanded = DiffText.ExpandTabs(text);
        var cell = DiffText.CellsBefore(expanded, charIndex);
        Assert.Equal(charIndex, DiffText.CharIndexAtCell(expanded, cell));
    }
}
