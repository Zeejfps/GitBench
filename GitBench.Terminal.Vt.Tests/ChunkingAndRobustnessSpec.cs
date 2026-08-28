using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The engine is the parse at the pseudo-terminal boundary, so it takes whatever the kernel hands
/// it: a sequence chopped in half by a read boundary, a UTF-8 character split across two batches,
/// junk from a program that set the wrong locale, a parameter longer than an int. None of that may
/// throw, because the alternative is a terminal that dies mid-session on a byte nobody chose.
/// </summary>
/// <remarks>
/// The chunking half of this is not hypothetical — a pseudo-terminal read returns whatever happens
/// to be in the pipe. <see cref="ChunkInvarianceTests"/> makes the same point over whole recorded
/// sessions; these cases name the sequence that broke.
/// </remarks>
public class ChunkingAndRobustnessSpec
{
    [Fact]
    public void SequenceSplitAcrossFeeds_IsAppliedOnce()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}3;");
        engine.Feed("5H*");

        Assert.Equal("    *", engine.RowText(2));
    }

    [Fact]
    public void EscapeAloneAtTheEndOfAFeed_IsHeldForTheNextOne()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Esc}");
        engine.Feed("[2;1H*");

        Assert.Equal("*", engine.RowText(1));
    }

    [Fact]
    public void WholeSessionFedOneByteAtATime_ProducesTheSameGrid()
    {
        var session = $"{Csi}2J{Csi}H{Csi}1;38;2;255;0;0mhello{Csi}0m\r\nworld{Csi}?25l{Osc}0;title{Bel}";

        using var whole = EngineUnderTest.Create(columns: 20, rows: 4);
        using var dribbled = EngineUnderTest.Create(columns: 20, rows: 4);

        whole.Feed(session);
        dribbled.FeedByteAtATime(session);

        Assert.Equal(whole.RowText(0), dribbled.RowText(0));
        Assert.Equal(whole.RowText(1), dribbled.RowText(1));
        Assert.Equal(whole.State, dribbled.State);
    }

    [Fact]
    public void MultiByteCharacterSplitAcrossFeeds_LandsAsOneRune()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        var utf8 = Bytes("é");
        engine.Feed(utf8.AsSpan(0, 1));
        engine.Feed(utf8.AsSpan(1));

        Assert.Equal("é", engine.RowText(0));
    }

    [Fact]
    public void EmptyFeed_ChangesNothing()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("abc");
        var result = engine.Feed(ReadOnlySpan<byte>.Empty);

        Assert.Equal(FeedResult.Nothing, result);
        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void TruncatedTruecolorSequence_DoesNotThrow()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        var thrown = Record.Exception(() => engine.Feed($"{Csi}38m{Csi}38;2m{Csi}38;2;1mX"));

        Assert.Null(thrown);
    }

    [Fact]
    public void UnknownFinalByte_IsDiscardedWithoutPrintingIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"a{Csi}1;2Qb");

        Assert.Equal("ab", engine.RowText(0));
    }

    [Fact]
    public void OverlongParameter_DoesNotThrowOrCorruptTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        var thrown = Record.Exception(() => engine.Feed($"{Csi}99999999999;99999999999H"));

        Assert.Null(thrown);
        Assert.InRange(engine.State.Cursor.Row, 0, 3);
        Assert.InRange(engine.State.Cursor.Column, 0, 9);
    }

    [Fact]
    public void InvalidUtf8_DoesNotThrowAndDoesNotSwallowTheTextAroundIt()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        var thrown = Record.Exception(() => engine.Feed(new byte[] { (byte)'a', 0xC3, 0x28, (byte)'b' }));

        Assert.Null(thrown);
        Assert.StartsWith("a", engine.RowText(0), StringComparison.Ordinal);
        Assert.EndsWith("b", engine.RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void UnterminatedOsc_DoesNotSwallowTheRestOfTheSession()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Osc}0;a title that never ends");
        engine.Feed($"{Bel}visible");

        Assert.Equal("visible", engine.RowText(0));
    }

    [Fact]
    public void NulBytes_AreIgnoredRatherThanPrinted()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed(new byte[] { (byte)'a', 0x00, (byte)'b' });

        Assert.Equal("ab", engine.RowText(0));
    }

    [Fact]
    public void ControlCharacterInsideASequence_IsExecutedAndTheSequenceContinues()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 4);

        engine.Feed($"{Csi}3;\r5H*");

        Assert.Equal("    *", engine.RowText(2));
    }
}
