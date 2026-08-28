using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// How a cell's width is spent: the margin a two-column character cannot straddle, the trailer it
/// leaves beside it, the cells an insertion pushes it through, and the runes that cost nothing.
/// </summary>
/// <remarks>
/// <see cref="UnicodeSpec"/> pins that a wide character is two columns at all. These are the
/// neighbouring cases, where a width the engine computes correctly can still be spent wrongly: a
/// leader written into the last column would put its trailer off the end of the row, and an
/// insertion that shifts a leader into it would do the same.
/// </remarks>
public class CharacterWidthSpec
{
    [Fact]
    public void WideCharacter_AtTheLastColumn_MovesWholeToTheNextRow()
    {
        using var engine = EngineUnderTest.Create(columns: 4, rows: 2);

        engine.Feed("abc漢");

        Assert.Equal("abc", engine.RowText(0));
        Assert.Equal("漢", engine.RowText(1));
        Assert.Equal(CellWidth.WideLeader, engine.CellAt(0, 1).Width);
        Assert.Equal(CellWidth.WideTrailer, engine.CellAt(1, 1).Width);
    }

    [Fact]
    public void RowContinuedByAWideCharacter_IsMarkedAsAContinuation()
    {
        using var engine = EngineUnderTest.Create(columns: 4, rows: 2);

        engine.Feed("abc漢");

        Assert.True(engine.Grid.ContinuesPreviousRow(1));
    }

    [Fact]
    public void TrailerCell_CarriesTheLeadersAttributes()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}4m漢");

        Assert.Equal(CellAttributes.Underline, engine.CellAt(1, 0).Attributes);
    }

    [Fact]
    public void WideCharacter_ShiftedPastTheLastColumn_LeavesNoHalfOfItBehind()
    {
        using var engine = EngineUnderTest.Create(columns: 4, rows: 2);

        engine.Feed($"ab漢{Csi}1;1H{Csi}4hX");

        Assert.Equal(CellWidth.Single, engine.CellAt(3, 0).Width);
    }

    [Fact]
    public void WideCharacter_OnAOneColumnScreen_IsDroppedRatherThanRunPastTheRow()
    {
        using var engine = EngineUnderTest.Create(columns: 1, rows: 2);

        var thrown = Record.Exception(() => engine.Feed("漢a"));

        Assert.Null(thrown);
        Assert.Equal(new Rune('a'), engine.CellAt(0, 1).Rune);
    }

    [Fact]
    public void ZeroWidthMark_BeforeAnyText_DoesNotTakeTheFirstColumn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("\u0301a");

        Assert.Equal(new Rune('a'), engine.CellAt(0, 0).Rune);
        Assert.Equal((1, 0), engine.CursorAt());
    }

    [Fact]
    public void NonBreakingSpace_TakesAColumnOfItsOwn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("a\u00a0b");

        Assert.Equal(new Rune('b'), engine.CellAt(2, 0).Rune);
    }
}
