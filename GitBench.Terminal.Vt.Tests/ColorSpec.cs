using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// SGR colour, which is where the grid surface earns its keep. Truecolor is the single most-used
/// styling sequence in the claude corpus (47 of them in five seconds) and git-log leans entirely on
/// the basic sixteen, so the cell has to carry all three colour spaces back out without collapsing
/// them onto each other.
/// </summary>
public class ColorSpec
{
    [Fact]
    public void TruecolorForeground_ReachesTheCellAsRgb()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}38;2;255;140;0mX");

        Assert.Equal(TerminalColor.Rgb(255, 140, 0), engine.CellAt(0, 0).Foreground);
    }

    [Fact]
    public void TruecolorBackground_ReachesTheCellAsRgb()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}48;2;16;32;48mX");

        Assert.Equal(TerminalColor.Rgb(16, 32, 48), engine.CellAt(0, 0).Background);
    }

    [Fact]
    public void TruecolorForegroundAndBackground_InOneSequence_BothReachTheCell()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}38;2;1;2;3;48;2;4;5;6mX");

        var cell = engine.CellAt(0, 0);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), cell.Foreground);
        Assert.Equal(TerminalColor.Rgb(4, 5, 6), cell.Background);
    }

    [Fact]
    public void TwoDistinctTruecolors_StayDistinctInTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}38;2;200;0;0mA{Csi}38;2;201;0;0mB");

        Assert.NotEqual(engine.CellAt(0, 0).Foreground, engine.CellAt(1, 0).Foreground);
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(34, 4)]
    [InlineData(37, 7)]
    public void BasicForeground_MapsToThePaletteIndexItNames(int parameter, byte index)
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}{parameter}mX");

        Assert.Equal(TerminalColor.Indexed(index), engine.CellAt(0, 0).Foreground);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(42, 2)]
    [InlineData(47, 7)]
    public void BasicBackground_MapsToThePaletteIndexItNames(int parameter, byte index)
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}{parameter}mX");

        Assert.Equal(TerminalColor.Indexed(index), engine.CellAt(0, 0).Background);
    }

    [Theory]
    [InlineData(90, 8)]
    [InlineData(97, 15)]
    public void BrightForeground_MapsToTheUpperEightPaletteEntries(int parameter, byte index)
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}{parameter}mX");

        Assert.Equal(TerminalColor.Indexed(index), engine.CellAt(0, 0).Foreground);
    }

    [Fact]
    public void IndexedForeground_ReachesTheCellAsThatIndex()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}38;5;196mX");

        Assert.Equal(TerminalColor.Indexed(196), engine.CellAt(0, 0).Foreground);
    }

    [Fact]
    public void DefaultForegroundParameter_ReturnsTheCellToTheThemeDefault()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}31mA{Csi}39mB");

        Assert.Equal(TerminalColor.Default, engine.CellAt(1, 0).Foreground);
    }

    [Fact]
    public void DefaultBackgroundParameter_ReturnsTheCellToTheThemeDefault()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}41mA{Csi}49mB");

        Assert.Equal(TerminalColor.Default, engine.CellAt(1, 0).Background);
    }

    [Fact]
    public void Reset_ReturnsBothTruecolorsToTheThemeDefault()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}38;2;9;9;9;48;2;8;8;8mA{Csi}0mB");

        var cell = engine.CellAt(1, 0);
        Assert.Equal(TerminalColor.Default, cell.Foreground);
        Assert.Equal(TerminalColor.Default, cell.Background);
    }

    [Fact]
    public void Colour_AppliesOnlyToCellsPrintedAfterIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"A{Csi}31mB");

        Assert.Equal(TerminalColor.Default, engine.CellAt(0, 0).Foreground);
        Assert.Equal(TerminalColor.Indexed(1), engine.CellAt(1, 0).Foreground);
    }

    [Fact]
    public void Colour_PersistsAcrossFeedCalls()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}31m");
        engine.Feed("X");

        Assert.Equal(TerminalColor.Indexed(1), engine.CellAt(0, 0).Foreground);
    }
}
