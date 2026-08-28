using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Scrolling and the scrollback window. Row 0 of the grid surface is the top of the live viewport
/// whatever has scrolled past; rows above it are negative. These tests pin that indexing, because
/// it is the part of the surface a replacement engine is most likely to get subtly wrong.
/// </summary>
public class ScrollbackSpec
{
    [Fact]
    public void LineFeedOnTheLastRow_ScrollsTheViewportUp()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("one\r\ntwo\r\nthree\r\nfour");

        Assert.Equal("two", engine.RowText(0));
        Assert.Equal("three", engine.RowText(1));
        Assert.Equal("four", engine.RowText(2));
    }

    [Fact]
    public void ATrailingNewline_ScrollsTooAndLeavesHistoryInOrder()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 3);

        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\n");

        Assert.Equal(2, engine.Grid.ScrollbackRows);
        Assert.Equal("one", engine.RowText(-2));
        Assert.Equal("two", engine.RowText(-1));
        Assert.Equal("three", engine.RowText(0));
        Assert.Equal("four", engine.RowText(1));
    }

    [Fact]
    public void ScrollbackRows_CountEveryLineThatLeftTheViewport()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("a\r\nb\r\nc\r\nd\r\ne");

        Assert.Equal(3, engine.Grid.ScrollbackRows);
        Assert.Equal("a", engine.RowText(-3));
        Assert.Equal("b", engine.RowText(-2));
        Assert.Equal("c", engine.RowText(-1));
    }

    [Fact]
    public void AWrappedLineThatScrolls_StillJoinsAcrossTheScrollbackBoundary()
    {
        using var engine = EngineUnderTest.Create(columns: 4, rows: 2);

        engine.Feed("abcdefghij");

        Assert.Equal(1, engine.Grid.ScrollbackRows);
        Assert.Equal("abcd", engine.RowText(-1));
        Assert.Equal("efgh", engine.RowText(0));
        Assert.Equal("ij", engine.RowText(1));

        Assert.False(engine.Grid.ContinuesPreviousRow(-1), "the first row of the line starts it");
        Assert.True(
            engine.Grid.ContinuesPreviousRow(0),
            "the top viewport row continues a row that has scrolled into history, and copying the "
            + "selection must not insert a newline at the boundary");
        Assert.True(
            engine.Grid.ContinuesPreviousRow(1),
            "the bottom viewport row is answerable too: it continues the row above it");
    }

    [Fact]
    public void ScrollbackRows_StartAtZero()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        Assert.Equal(0, engine.Grid.ScrollbackRows);
    }

    [Fact]
    public void ReadingAboveTheScrollback_Throws()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);
        var destination = new TerminalCell[10];

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Grid.CopyRow(-1, destination));
    }

    [Fact]
    public void ReadingBelowTheViewport_Throws()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);
        var destination = new TerminalCell[10];

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Grid.CopyRow(3, destination));
    }

    [Fact]
    public void ReadingPastTheLastColumn_Throws()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Grid.Cell(10, 0));
    }

    [Fact]
    public void CopyingIntoATooShortDestination_Throws()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);
        var destination = new TerminalCell[9];

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Grid.CopyRow(0, destination));
    }

    [Fact]
    public void ReverseIndexOnTheTopRow_ScrollsDownAndBlanksTheTop()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"one\r\ntwo\r\nthree{Csi}1;1H{Esc}M");

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal("one", engine.RowText(1));
        Assert.Equal("two", engine.RowText(2));
    }

    [Fact]
    public void ScrollRegion_ConfinesScrollingToItsRows()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 5);
        engine.Feed("top\r\na\r\nb\r\nc\r\nbottom");

        engine.Feed($"{Csi}2;4r{Csi}4;1H\n");

        Assert.Equal("top", engine.RowText(0));
        Assert.Equal("bottom", engine.RowText(4));
        Assert.Equal("b", engine.RowText(1));
    }

    [Fact]
    public void Resize_KeepsTheGridReadableAtTheNewSize()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("hello");
        engine.Resize(new TerminalSize(20, 5));

        Assert.Equal(new TerminalSize(20, 5), engine.Grid.Size);
        Assert.Equal("hello", engine.RowText(0));
    }
}
