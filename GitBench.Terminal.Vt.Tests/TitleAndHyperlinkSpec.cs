using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// OSC. Every corpus sets a title (claude does it eight times in five seconds) and claude alone
/// emits OSC 8 hyperlinks. The load-bearing property of an unhandled OSC is that its payload must
/// not leak into the grid as text — a URL printed across the user's screen is worse than a
/// hyperlink that is not clickable.
/// </summary>
public class TitleAndHyperlinkSpec
{
    [Fact]
    public void Title_StartsEmpty()
    {
        using var engine = EngineUnderTest.Create();

        Assert.Equal(string.Empty, engine.State.Title);
    }

    [Fact]
    public void Osc0_SetsBothTitleAndIconName()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}0;my session{Bel}");

        Assert.Equal("my session", engine.State.Title);
        Assert.Equal("my session", engine.State.IconTitle);
    }

    [Fact]
    public void Osc2_SetsOnlyTheWindowTitle()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}1;icon{Bel}{Osc}2;window{Bel}");

        Assert.Equal("window", engine.State.Title);
        Assert.Equal("icon", engine.State.IconTitle);
    }

    [Fact]
    public void Osc_TerminatedByStringTerminator_IsAcceptedLikeBell()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Osc}2;titled{St}rest");

        Assert.Equal("titled", engine.State.Title);
        Assert.Equal("rest", engine.RowText(0));
    }

    [Fact]
    public void Title_IsNotPrintedToTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 40, rows: 2);

        engine.Feed($"{Osc}0;C:\\Windows\\system32\\cmd.exe{Bel}");

        Assert.Equal(string.Empty, engine.RowText(0));
    }

    [Fact]
    public void Title_IsReplacedByTheNextOsc()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}0;first{Bel}{Osc}0;second{Bel}");

        Assert.Equal("second", engine.State.Title);
    }

    [Fact]
    public void Title_MayContainNonAsciiText()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}0;naïve — Ω{Bel}");

        Assert.Equal("naïve — Ω", engine.State.Title);
    }

    [Fact]
    public void Osc8_PrintsTheLinkTextAndNotTheUrl()
    {
        using var engine = EngineUnderTest.Create(columns: 40, rows: 2);

        engine.Feed($"{Osc}8;;https://example.com/docs{St}docs{Osc}8;;{St}");

        Assert.Equal("docs", engine.RowText(0));
    }

    [Fact]
    public void UnhandledOsc_DoesNotLeakItsPayloadIntoTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 40, rows: 2);

        engine.Feed($"{Osc}52;c;c2VjcmV0{St}after");

        Assert.Equal("after", engine.RowText(0));
    }

    [Fact]
    public void Osc_SplitAcrossFeeds_StillSetsTheTitle()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}0;my se");
        engine.Feed($"ssion{Bel}");

        Assert.Equal("my session", engine.State.Title);
    }

    [Fact]
    public void Osc_SplitBeforeItsTerminator_StillSetsTheTitle()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Osc}0;my session");
        engine.Feed(Bel);

        Assert.Equal("my session", engine.State.Title);
    }
}
