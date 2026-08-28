using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// EL, ED and ECH. EL is the single most-used sequence after CR/LF in every corpus — 83 in claude,
/// 95 in vim, 66 in less — because a TUI repaints a line by erasing it and redrawing. Getting the
/// erase extents wrong leaves ghost text that survives every subsequent frame.
/// </summary>
public class EraseSpec
{
    [Fact]
    public void EraseInLine_Default_ClearsFromTheCursorToTheEndOfTheRow()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;4H{Csi}K");

        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void EraseInLine_ToStart_ClearsUpToAndIncludingTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;4H{Csi}1K");

        Assert.Equal("    ef", engine.RowText(0));
    }

    [Fact]
    public void EraseInLine_Whole_ClearsTheRowAndLeavesTheCursorWhereItWas()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;4H{Csi}2K");

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal((3, 0), engine.CursorAt());
    }

    [Fact]
    public void EraseInLine_LeavesOtherRowsAlone()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"one\r\ntwo{Csi}1;1H{Csi}2K");

        Assert.Equal("two", engine.RowText(1));
    }

    [Fact]
    public void EraseInLine_RestoresCellsToBlank_NotJustToSpaces()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}31mred{Csi}1;1H{Csi}2K");

        Assert.Equal(TerminalCell.Blank, engine.CellAt(0, 0));
    }

    [Fact]
    public void EraseInDisplay_Default_ClearsFromTheCursorToTheEndOfTheScreen()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"one\r\ntwo\r\nthree{Csi}2;2H{Csi}J");

        Assert.Equal("one", engine.RowText(0));
        Assert.Equal("t", engine.RowText(1));
        Assert.Equal(string.Empty, engine.RowText(2));
    }

    [Fact]
    public void EraseInDisplay_ToStart_ClearsEverythingUpToAndIncludingTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"one\r\ntwo\r\nthree{Csi}2;2H{Csi}1J");

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal("  o", engine.RowText(1));
        Assert.Equal("three", engine.RowText(2));
    }

    [Fact]
    public void EraseInDisplay_Whole_ClearsEveryRowWithoutMovingTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"one\r\ntwo\r\nthree{Csi}2;2H{Csi}2J");

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal(string.Empty, engine.RowText(1));
        Assert.Equal(string.Empty, engine.RowText(2));
        Assert.Equal((1, 1), engine.CursorAt());
    }

    [Fact]
    public void EraseCharacters_BlanksInPlaceWithoutShiftingTheRestOfTheRow()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;2H{Csi}3X");

        Assert.Equal("a   ef", engine.RowText(0));
    }

    [Fact]
    public void EraseCharacters_DoesNotMoveTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;2H{Csi}3X");

        Assert.Equal((1, 0), engine.CursorAt());
    }

    [Fact]
    public void EraseCharacters_PastTheRightEdge_StopsAtTheEndOfTheRow()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;5H{Csi}99X");

        Assert.Equal("abcd", engine.RowText(0));
    }

    [Fact]
    public void DeleteCharacters_ShiftsTheRestOfTheRowLeft()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"abcdef{Csi}1;2H{Csi}2P");

        Assert.Equal("adef", engine.RowText(0));
    }
}
