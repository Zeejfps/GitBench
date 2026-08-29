using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The damage span is the whole reason the renderer can keep up with a streaming TUI: a token
/// arriving on one line must not cost a full-screen repaint. These tests pin the two properties a
/// renderer relies on — damage is reported for every row that changed (never too small, which would
/// leave stale pixels) and is not reported for rows that did not (never the whole screen every
/// time, which would make the span useless).
/// </summary>
public class DamageSpec
{
    [Fact]
    public void FeedThatChangesNothing_ReportsNoDamage()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        var result = engine.Feed($"{Csi}?2004h");

        Assert.True(result.Damage.IsEmpty, $"A mode change touched no cell but reported {result.Damage}.");
    }

    [Fact]
    public void PrintingOnOneRow_DamagesThatRow()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        engine.Feed($"{Csi}3;1H");
        var result = engine.Feed("hello");

        Assert.Equal(new RowSpan(2, 2), result.Damage);
    }

    [Fact]
    public void PrintingOnTwoRows_DamagesBoth()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        var result = engine.Feed("one\r\ntwo");

        Assert.Equal(new RowSpan(0, 1), result.Damage);
    }

    [Fact]
    public void ClearingTheScreen_DamagesEveryRow()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        engine.Feed("a\r\nb\r\nc");
        var result = engine.Feed($"{Csi}2J");

        Assert.True(
            result.Damage.Covers(engine.Grid.Size.Rows),
            $"ED 2 repaints the whole viewport but reported {result.Damage}.");
    }

    [Fact]
    public void SwitchingToTheAltScreen_DamagesEveryRow()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        engine.Feed("shell output");
        var result = engine.Feed($"{Csi}?1049h");

        Assert.True(
            result.Damage.Covers(engine.Grid.Size.Rows),
            $"An alt-screen switch replaces the whole viewport but reported {result.Damage}.");
    }

    [Fact]
    public void ScrollingTheViewport_DamagesEveryRow()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 3);

        engine.Feed("a\r\nb\r\nc");
        var result = engine.Feed("\r\nd");

        Assert.True(
            result.Damage.Covers(engine.Grid.Size.Rows),
            $"A scroll moves every row's content but reported {result.Damage}.");
    }

    [Fact]
    public void DamageIsScopedToTheFeedThatCausedIt()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        engine.Feed($"{Csi}5;1Hbottom");
        var result = engine.Feed($"{Csi}?25l");

        Assert.True(
            result.Damage.IsEmpty,
            $"Damage from an earlier feed was reported again as {result.Damage}.");
    }

    [Fact]
    public void ScrollingTheViewport_ReportsTheLinesThatLeftIt()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 3);

        engine.Feed("a\r\nb\r\nc");
        var result = engine.Feed("\r\nd\r\ne");

        Assert.Equal(2, result.LinesScrolled);
    }

    [Fact]
    public void AFeedThatDoesNotScroll_ReportsNoLinesScrolled()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 6);

        var result = engine.Feed("a\r\nb");

        Assert.Equal(0, result.LinesScrolled);
    }

    [Fact]
    public void OnceTheHistoryIsFull_LinesStillCountAsScrolled()
    {
        // The count is what a reader scrolled back through the history follows to stay on the line
        // they were reading. A full history stops growing and drops its oldest line instead, so the
        // depth it reports is not the count and a reader following that would drift a line per line
        // of output for the rest of the session.
        using var engine = EngineUnderTest.Create(new TerminalSetup(new TerminalSize(20, 3), 2));

        var scrolled = 0;
        for (var line = 0; line < 10; line++)
            scrolled += engine.Feed(line == 0 ? "l0" : $"\r\nl{line}").LinesScrolled;

        // Ten lines into a three-row screen: the first three fill it and the other seven push a
        // line off the top each, of which the two-line history can only still be holding two.
        Assert.Equal(7, scrolled);
        Assert.Equal(2, engine.Grid.ScrollbackRows);
    }
}
