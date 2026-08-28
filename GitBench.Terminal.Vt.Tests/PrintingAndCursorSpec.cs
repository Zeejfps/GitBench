using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Where runes land and where the cursor ends up. CR, LF, CUP and CUF are the four most-used
/// sequences across all the recorded corpora — 84 CRs and 58 CUPs in a five-second claude session —
/// so anything wrong here is wrong on every frame.
/// </summary>
/// <remarks>Assertions state correct xterm behaviour, not what any particular engine does.</remarks>
public class PrintingAndCursorSpec
{
    [Fact]
    public void Print_PutsEachRuneInSuccessiveCells()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("abc");

        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void Print_LeavesTheCursorPastTheLastRune()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("abc");

        Assert.Equal((3, 0), engine.CursorAt());
    }

    [Fact]
    public void NewTerminal_IsBlankWithTheCursorHome()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal((0, 0), engine.CursorAt());
        Assert.Equal(TerminalCell.Blank, engine.CellAt(0, 0));
    }

    [Fact]
    public void CarriageReturn_MovesToColumnZeroWithoutErasing()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("abc\r");

        Assert.Equal((0, 0), engine.CursorAt());
        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void CarriageReturnThenPrint_OverwritesFromTheStartOfTheLine()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("abcdef\rXY");

        Assert.Equal("XYcdef", engine.RowText(0));
    }

    [Fact]
    public void LineFeed_MovesDownAndKeepsTheColumn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("ab\nc");

        Assert.Equal("ab", engine.RowText(0));
        Assert.Equal("  c", engine.RowText(1));
    }

    [Fact]
    public void CursorPosition_IsOneBasedInTheSequenceAndZeroBasedInTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}3;5H*");

        Assert.Equal("    *", engine.RowText(2));
    }

    [Fact]
    public void CursorPosition_WithNoParameters_GoesHome()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"line0\nline1{Csi}H");

        Assert.Equal((0, 0), engine.CursorAt());
    }

    [Fact]
    public void CursorPosition_BeyondTheGrid_ClampsToTheLastCell()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}99;99H");

        Assert.Equal((9, 3), engine.CursorAt());
    }

    [Fact]
    public void CursorForward_WithNoParameter_MovesOneColumn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"a{Csi}Cb");

        Assert.Equal("a b", engine.RowText(0));
    }

    [Fact]
    public void CursorForward_SkipsCellsWithoutErasingThem()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"abcdef{Csi}H{Csi}3CX");

        Assert.Equal("abcXef", engine.RowText(0));
    }

    [Fact]
    public void CursorForward_PastTheRightEdge_StopsAtTheLastColumn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"{Csi}99C");

        Assert.Equal((9, 0), engine.CursorAt());
    }

    [Fact]
    public void CursorBackward_PastTheLeftEdge_StopsAtColumnZero()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"abc{Csi}99D");

        Assert.Equal((0, 0), engine.CursorAt());
    }

    [Fact]
    public void CursorUp_AtTheTopRow_DoesNotScroll()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"top{Csi}5A");

        Assert.Equal(0, engine.State.Cursor.Row);
        Assert.Equal("top", engine.RowText(0));
        Assert.Equal(0, engine.Grid.ScrollbackRows);
    }

    [Fact]
    public void Backspace_MovesLeftWithoutErasing()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed("abc\b");

        Assert.Equal((2, 0), engine.CursorAt());
        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void Tab_AdvancesToTheNextEightColumnStop()
    {
        using var engine = EngineUnderTest.Create(columns: 24, rows: 3);

        engine.Feed("ab\tX");

        Assert.Equal("ab      X", engine.RowText(0));
    }

    [Fact]
    public void Print_PastTheRightMargin_ContinuesOnTheNextRowAndMarksIt()
    {
        using var engine = EngineUnderTest.Create(columns: 4, rows: 3);

        engine.Feed("abcdef");

        Assert.Equal("abcd", engine.RowText(0));
        Assert.Equal("ef", engine.RowText(1));
        Assert.True(
            engine.Grid.ContinuesPreviousRow(1),
            "row 1 continues row 0, so copying the two must not insert a newline");
        Assert.False(
            engine.Grid.ContinuesPreviousRow(0),
            "row 0 starts a line of its own");
    }

    [Fact]
    public void SaveAndRestoreCursor_ReturnsToTheSavedCell()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}2;3H{Esc}7{Csi}4;1H{Esc}8");

        Assert.Equal((2, 1), engine.CursorAt());
    }
}
