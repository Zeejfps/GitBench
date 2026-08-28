namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// What a cell is allowed to hold. A terminal cell is a grapheme cluster occupying one or two
/// columns, not a codepoint occupying one — and the column arithmetic every other module does is
/// only correct if the engine has already decided the width. If the engine gets this wrong the
/// renderer cannot recover it: a wide character counted as one column desynchronises the rest of
/// the row for as long as it is on screen.
/// </summary>
public class UnicodeSpec
{
    [Fact]
    public void LatinOneCharacter_OccupiesOneCell()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("naïve");

        Assert.Equal("naïve", engine.RowText(0));
        Assert.Equal((5, 0), engine.CursorAt());
    }

    [Fact]
    public void BoxDrawingCharacters_OccupyOneCellEach()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("╭──╮");

        Assert.Equal("╭──╮", engine.RowText(0));
        Assert.Equal((4, 0), engine.CursorAt());
    }

    [Fact]
    public void WideCharacter_OccupiesTwoColumns()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("漢");

        Assert.Equal(CellWidth.WideLeader, engine.CellAt(0, 0).Width);
        Assert.Equal((2, 0), engine.CursorAt());
    }

    [Fact]
    public void WideCharacter_LeavesATrailerCellBesideIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("漢");

        Assert.Equal(CellWidth.WideTrailer, engine.CellAt(1, 0).Width);
    }

    [Fact]
    public void TextAfterAWideCharacter_StartsTwoColumnsLater()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("漢x");

        Assert.Equal("漢x", engine.RowText(0));
        Assert.Equal(new Rune('x'), engine.CellAt(2, 0).Rune);
    }

    [Fact]
    public void CombiningMark_JoinsTheCellItFollowsRatherThanTakingItsOwn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("e\u0301x");

        Assert.Equal((2, 0), engine.CursorAt());
        Assert.Equal("e\u0301", engine.CellAt(0, 0).Text);
    }

    [Fact]
    public void AstralCharacter_IsOneRuneNotTwoCells()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("𝄞");

        Assert.Equal(new Rune(0x1D11E), engine.CellAt(0, 0).Rune);
    }

    [Fact]
    public void ZeroWidthSpace_DoesNotConsumeAColumn()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("a\u200bb");

        Assert.Equal(new Rune('b'), engine.CellAt(1, 0).Rune);
    }
}
