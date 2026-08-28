using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The bytes the engine owes the program. These come back on the <see cref="FeedResult"/> that
/// <see cref="ITerminalEngine.Feed"/> returns rather than through an event or a second call, so a
/// caller cannot lose a reply by wiring up late or by forgetting to drain, and a test needs no fake
/// delegate to see one.
/// </summary>
/// <remarks>
/// On Windows conhost answers DSR and DA1 before they reach us, so these sequences are rare in a
/// Windows corpus; they are not rare on a Unix pseudo-terminal, and a program that blocks waiting
/// for a reply hangs the session.
/// </remarks>
public class ResponseSpec
{
    [Fact]
    public void OrdinaryOutput_ProducesNoResponse()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed("hello\r\n");

        Assert.False(result.HasResponse, $"Unexpected reply: {result.Printable()}");
    }

    [Fact]
    public void CursorPositionReport_AnswersWithTheOneBasedCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        var result = engine.Feed($"{Csi}3;5H{Csi}6n");

        Assert.Equal($"{Csi}3;5R", result.Text());
    }

    [Fact]
    public void CursorPositionReport_FromHome_ReportsOneOne()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}6n");

        Assert.Equal($"{Csi}1;1R", result.Text());
    }

    [Fact]
    public void StatusReport_AnswersOk()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}5n");

        Assert.Equal($"{Csi}0n", result.Text());
    }

    [Fact]
    public void PrimaryDeviceAttributes_AnswersWithADecPrivateReport()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}c");

        var response = result.Text();
        Assert.True(
            response.StartsWith($"{Csi}?", StringComparison.Ordinal) && response.EndsWith('c'),
            $"DA1 must answer CSI ? ... c so the program can finish its capability probe; got '{result.Printable()}'.");
    }

    [Fact]
    public void SecondaryDeviceAttributes_AnswersWithAVersionReport()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}>c");

        var response = result.Text();
        Assert.True(
            response.StartsWith($"{Csi}>", StringComparison.Ordinal) && response.EndsWith('c'),
            $"DA2 must answer CSI > Pp;Pv;Pc c; got '{result.Printable()}'.");
    }

    [Fact]
    public void Response_IsScopedToTheFeedThatProvokedIt()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}6n");
        var second = engine.Feed("plain text");

        Assert.False(second.HasResponse, $"A stale reply was repeated: {second.Printable()}");
    }

    [Fact]
    public void TwoQueriesInOneFeed_AnswerInOrderInOneResponse()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}5n{Csi}6n");

        Assert.Equal($"{Csi}0n{Csi}1;1R", result.Text());
    }

    [Fact]
    public void QuerySplitAcrossFeeds_StillAnswersOnce()
    {
        using var engine = EngineUnderTest.Create();

        var first = engine.Feed($"{Csi}6");
        var second = engine.Feed("n");

        Assert.False(first.HasResponse);
        Assert.Equal($"{Csi}1;1R", second.Text());
    }

    [Fact]
    public void QueryDoesNotPrintItsOwnBytesToTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Csi}6n{Csi}c");

        Assert.Equal(string.Empty, engine.RowText(0));
    }
}
