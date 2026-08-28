using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// What a resize does to lines that ran past the right margin. Reflow is the one path that moves a
/// cell from one row to another after it was printed, so it is where a change to the cell
/// representation shows up as text landing in the wrong place or losing its colour.
/// </summary>
/// <remarks>
/// Every case parks the cursor off the wrapped line before resizing: an xterm-class reflow leaves
/// the line the cursor sits on alone, because the program owns fixing that one up itself.
/// </remarks>
public class ReflowSpec
{
    [Fact]
    public void Narrowing_SplitsAWrappedLineOntoMoreRows()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed("abcdefghijklmno\r\n");

        engine.Resize(new TerminalSize(5, 4));

        Assert.Equal("abcde", engine.RowText(0));
        Assert.Equal("fghij", engine.RowText(1));
        Assert.Equal("klmno", engine.RowText(2));
    }

    [Fact]
    public void Narrowing_MarksEveryRowThatContinuesTheOneAboveIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed("abcdefghijklmno\r\n");

        engine.Resize(new TerminalSize(5, 4));

        Assert.False(engine.Grid.ContinuesPreviousRow(0), "the first row of the line starts it");
        Assert.True(engine.Grid.ContinuesPreviousRow(1));
        Assert.True(engine.Grid.ContinuesPreviousRow(2));
    }

    [Fact]
    public void Narrowing_CarriesEachCellsColourWithIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed($"{Csi}31mabcdefghij{Csi}32mklmno{Csi}0m\r\n");

        engine.Resize(new TerminalSize(5, 4));

        Assert.Equal(TerminalColor.Indexed(1), engine.CellAt(0, 0).Foreground);
        Assert.Equal(TerminalColor.Indexed(1), engine.CellAt(4, 1).Foreground);
        Assert.Equal(TerminalColor.Indexed(2), engine.CellAt(0, 2).Foreground);
        Assert.Equal(TerminalColor.Indexed(2), engine.CellAt(4, 2).Foreground);
    }

    [Fact]
    public void Narrowing_CarriesEachCellsAttributesWithIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed($"{Csi}1mabcdefghij{Csi}22;4mklmno{Csi}0m\r\n");

        engine.Resize(new TerminalSize(5, 4));

        Assert.Equal(CellAttributes.Bold, engine.CellAt(0, 0).Attributes);
        Assert.Equal(CellAttributes.Bold, engine.CellAt(4, 1).Attributes);
        Assert.Equal(CellAttributes.Underline, engine.CellAt(0, 2).Attributes);
    }

    [Fact]
    public void Widening_RejoinsAWrappedLineOntoOneRow()
    {
        using var engine = EngineUnderTest.Create(columns: 5, rows: 4);
        engine.Feed("abcdefghijklmno\r\n");

        engine.Resize(new TerminalSize(15, 4));

        Assert.Equal("abcdefghijklmno", engine.RowText(0));
        Assert.Equal(string.Empty, engine.RowText(1));
        Assert.False(engine.Grid.ContinuesPreviousRow(1));
    }

    [Fact]
    public void Widening_CarriesEachCellsColourWithIt()
    {
        using var engine = EngineUnderTest.Create(columns: 5, rows: 4);
        engine.Feed($"{Csi}31mabcde{Csi}32mfghij{Csi}34mklmno{Csi}0m\r\n");

        engine.Resize(new TerminalSize(15, 4));

        Assert.Equal(TerminalColor.Indexed(1), engine.CellAt(0, 0).Foreground);
        Assert.Equal(TerminalColor.Indexed(2), engine.CellAt(5, 0).Foreground);
        Assert.Equal(TerminalColor.Indexed(4), engine.CellAt(14, 0).Foreground);
    }

    [Fact]
    public void ErasedCells_KeepTheBackgroundTheyWereErasedWithAcrossAResize()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed($"{Csi}44m{Csi}2K{Csi}0m");

        engine.Resize(new TerminalSize(20, 4));

        Assert.Equal(TerminalColor.Indexed(4), engine.CellAt(9, 0).Background);
    }

    [Fact]
    public void ErasedCells_AreNotTextButStillCountAsAPaintedRow()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}44m{Csi}2K{Csi}0m");

        Assert.Equal(string.Empty, engine.RowText(0));
        Assert.Equal(TerminalColor.Indexed(4), engine.CellAt(0, 0).Background);
    }

    [Fact]
    public void Resize_KeepsTheHistoryAboveTheViewportReadable()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);
        engine.Feed("one\r\ntwo\r\nthree\r\n");

        engine.Resize(new TerminalSize(10, 3));

        Assert.Equal("one", engine.RowText(-engine.Grid.ScrollbackRows));
    }

    [Fact]
    public void ResizeToTheSameSize_ChangesNothing()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);
        engine.Feed($"{Csi}31mabcdefghijklmno{Csi}0m\r\n");
        var before = GridSnapshot.Capture(engine, "before");

        engine.Resize(new TerminalSize(10, 4));

        Assert.Equal(before.ToText(), GridSnapshot.Capture(engine, "before").ToText());
    }
}
