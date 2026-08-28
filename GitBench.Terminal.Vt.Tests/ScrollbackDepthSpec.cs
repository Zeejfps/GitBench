namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// <see cref="TerminalSetup.ScrollbackLines"/> as a contract rather than an engine's private
/// choice. Two engines with different built-in depths disagree about what a recorded session looks
/// like, so a caller that asks for a depth has to get it — including a depth of zero, which is what
/// a caller keeping its own history asks for.
/// </summary>
public class ScrollbackDepthSpec
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void ScrollbackLines_BoundsTheHistoryTheGridKeeps(int depth)
    {
        using var engine = Engine(depth);

        for (var line = 0; line < 40; line++)
            engine.Feed($"line {line}\r\n");

        Assert.Equal(depth, engine.Grid.ScrollbackRows);
    }

    [Fact]
    public void ScrollbackLines_KeepsTheMostRecentLinesAndDropsTheOldest()
    {
        using var engine = Engine(depth: 2);

        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n");

        Assert.Equal("three", engine.RowText(-2));
        Assert.Equal("four", engine.RowText(-1));
        Assert.Equal("five", engine.RowText(0));
    }

    [Fact]
    public void ScrollbackLines_Zero_LeavesNothingAboveTheViewport()
    {
        using var engine = Engine(depth: 0);

        engine.Feed("one\r\ntwo\r\nthree\r\n");

        Assert.Equal(0, engine.Grid.ScrollbackRows);
        Assert.Equal("three", engine.RowText(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.RowText(-1));
    }

    static ITerminalEngine Engine(int depth) => TerminalEngines.Create(
        TerminalEngines.XtermSharp,
        new TerminalSetup(new TerminalSize(20, 2), ScrollbackLines: depth));
}
