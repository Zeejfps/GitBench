using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The non-colour half of SGR. Bold and its cancel (22) dominate git-log — 60 of each — and vim
/// leans on inverse for its status line, so the paired set/unset parameters matter as much as the
/// set ones.
/// </summary>
public class AttributeSpec
{
    [Theory]
    [InlineData(1, CellAttributes.Bold)]
    [InlineData(2, CellAttributes.Dim)]
    [InlineData(3, CellAttributes.Italic)]
    [InlineData(4, CellAttributes.Underline)]
    [InlineData(5, CellAttributes.Blink)]
    [InlineData(7, CellAttributes.Inverse)]
    [InlineData(8, CellAttributes.Hidden)]
    [InlineData(9, CellAttributes.CrossedOut)]
    public void Attribute_ReachesTheCellsPrintedAfterIt(int parameter, CellAttributes expected)
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}{parameter}mX");

        Assert.True(
            engine.CellAt(0, 0).Has(expected),
            $"SGR {parameter} should set {expected}; cell was {engine.CellAt(0, 0)}.");
    }

    [Theory]
    [InlineData(1, 22, CellAttributes.Bold)]
    [InlineData(2, 22, CellAttributes.Dim)]
    [InlineData(3, 23, CellAttributes.Italic)]
    [InlineData(4, 24, CellAttributes.Underline)]
    [InlineData(5, 25, CellAttributes.Blink)]
    [InlineData(7, 27, CellAttributes.Inverse)]
    [InlineData(9, 29, CellAttributes.CrossedOut)]
    public void CancellingParameter_ClearsTheAttributeItPairsWith(int set, int unset, CellAttributes attribute)
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}{set}mA{Csi}{unset}mB");

        Assert.True(
            engine.CellAt(0, 0).Has(attribute),
            $"SGR {set} should have set {attribute} first; cell was {engine.CellAt(0, 0)}.");
        Assert.False(
            engine.CellAt(1, 0).Has(attribute),
            $"SGR {unset} should clear {attribute}; cell was {engine.CellAt(1, 0)}.");
    }

    [Fact]
    public void NormalIntensity_ClearsBoldAndDimTogether()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}1;2mA{Csi}22mB");

        Assert.Equal(CellAttributes.None, engine.CellAt(1, 0).Attributes);
    }

    [Fact]
    public void Attributes_InOneSequence_AreAllCarriedAsBits()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}1;3;4;7;9mX");

        Assert.Equal(
            CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Underline
            | CellAttributes.Inverse | CellAttributes.CrossedOut,
            engine.CellAt(0, 0).Attributes);
    }

    [Fact]
    public void Reset_ReturnsTheWholeCellToItsDefault()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}1;4;31;42mA{Csi}0mB");

        Assert.Equal(TerminalCell.Blank with { Rune = new Rune('B') }, engine.CellAt(1, 0));
    }

    [Fact]
    public void EmptyParameter_IsTreatedAsReset()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}1;31mA{Csi}mB");

        var cell = engine.CellAt(1, 0);
        Assert.Equal(CellAttributes.None, cell.Attributes);
        Assert.Equal(TerminalColor.Default, cell.Foreground);
    }

    [Fact]
    public void ColourAndAttribute_InOneSequence_BothReachTheCell()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}1;38;2;10;20;30mX");

        var cell = engine.CellAt(0, 0);
        Assert.True(cell.Has(CellAttributes.Bold));
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cell.Foreground);
    }
}
