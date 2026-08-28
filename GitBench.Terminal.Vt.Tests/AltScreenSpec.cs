using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The alternate screen. Every full-screen program in the corpora enters and leaves it exactly once
/// — claude, vim and less all bracket their whole session in <c>?1049</c> — so the visible bug when
/// it is wrong is that quitting the program leaves its painting on top of the shell's scrollback.
/// </summary>
public class AltScreenSpec
{
    [Fact]
    public void AltScreen_StartsInactive()
    {
        using var engine = EngineUnderTest.Create();

        Assert.False(engine.State.Modes.AlternateScreen);
    }

    [Fact]
    public void EnteringAltScreen_SetsTheFlag()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1049h");

        Assert.True(engine.State.Modes.AlternateScreen);
    }

    [Fact]
    public void EnteringAltScreen_ShowsAnEmptyGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"shell{Csi}?1049h");

        Assert.Equal(string.Empty, engine.RowText(0));
    }

    [Fact]
    public void LeavingAltScreen_RestoresTheNormalScreenContent()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"shell{Csi}?1049h{Csi}Happ{Csi}?1049l");

        Assert.False(engine.State.Modes.AlternateScreen);
        Assert.Equal("shell", engine.RowText(0));
    }

    [Fact]
    public void LeavingAltScreen_RestoresTheCursorItSavedOnEntry()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"{Csi}2;4H{Csi}?1049h{Csi}1;1Happ{Csi}?1049l");

        Assert.Equal((3, 1), engine.CursorAt());
    }

    [Fact]
    public void AltScreen_KeepsNoScrollback()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}?1049h");
        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\n");

        Assert.Equal(0, engine.Grid.ScrollbackRows);
    }

    [Fact]
    public void NormalScreenScrollback_SurvivesAnAltScreenVisit()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed("one\r\ntwo\r\nthree\r\n");
        var before = engine.Grid.ScrollbackRows;

        engine.Feed($"{Csi}?1049happ{Csi}?1049l");

        Assert.Equal(before, engine.Grid.ScrollbackRows);
    }

    [Fact]
    public void ReEnteringAltScreen_DoesNotShowThePreviousVisitsContent()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 3);

        engine.Feed($"{Csi}?1049hfirst{Csi}?1049l");
        engine.Feed($"{Csi}?1049h");

        Assert.Equal(string.Empty, engine.RowText(0));
    }
}
