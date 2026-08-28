using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The DEC private modes the corpora actually use, read back through <see cref="TerminalState"/>.
/// Every one of these is a contract with a module other than the renderer: the input encoder needs
/// bracketed paste and application-cursor mode, the mouse encoder needs the tracking and encoding
/// pair, the frame scheduler needs synchronized output. A mode the engine swallows is a feature the
/// rest of the terminal silently loses.
/// </summary>
public class ModeSpec
{
    [Fact]
    public void CursorVisibility_StartsVisible()
    {
        using var engine = EngineUnderTest.Create();

        Assert.True(engine.State.Cursor.Visible);
    }

    [Fact]
    public void HideCursor_ClearsCursorVisible()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?25l");

        Assert.False(engine.State.Cursor.Visible);
    }

    [Fact]
    public void ShowCursor_AfterHiding_RestoresCursorVisible()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?25l{Csi}?25h");

        Assert.True(engine.State.Cursor.Visible);
    }

    [Fact]
    public void HidingTheCursor_DoesNotMoveIt()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"abc{Csi}?25l");

        Assert.Equal((3, 0), engine.CursorAt());
    }

    [Fact]
    public void BracketedPaste_StartsOff()
    {
        using var engine = EngineUnderTest.Create();

        Assert.False(engine.State.Modes.BracketedPaste);
    }

    [Fact]
    public void BracketedPaste_TracksSetAndReset()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?2004h");
        Assert.True(engine.State.Modes.BracketedPaste);

        engine.Feed($"{Csi}?2004l");
        Assert.False(engine.State.Modes.BracketedPaste);
    }

    [Fact]
    public void ApplicationCursorKeys_TracksSetAndReset()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1h");
        Assert.True(engine.State.Modes.ApplicationCursorKeys);

        engine.Feed($"{Csi}?1l");
        Assert.False(engine.State.Modes.ApplicationCursorKeys);
    }

    [Fact]
    public void ApplicationKeypad_TracksTheKeypadEscapes()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Esc}=");
        Assert.True(engine.State.Modes.ApplicationKeypad);

        engine.Feed($"{Esc}>");
        Assert.False(engine.State.Modes.ApplicationKeypad);
    }

    [Fact]
    public void FocusReporting_TracksSetAndReset()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1004h");
        Assert.True(engine.State.Modes.FocusReporting);

        engine.Feed($"{Csi}?1004l");
        Assert.False(engine.State.Modes.FocusReporting);
    }

    [Theory]
    [InlineData(1000, MouseTracking.Normal)]
    [InlineData(1002, MouseTracking.ButtonEvent)]
    [InlineData(1003, MouseTracking.AnyEvent)]
    public void MouseTracking_ReportsTheModeThatWasRequested(int mode, MouseTracking expected)
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?{mode}h");

        Assert.Equal(expected, engine.State.Modes.MouseTracking);
    }

    [Fact]
    public void MouseTracking_ResetsToOff()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1003h{Csi}?1003l");

        Assert.Equal(MouseTracking.Off, engine.State.Modes.MouseTracking);
    }

    [Fact]
    public void MouseTracking_LaterModeSupersedesTheEarlierOne()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1000h{Csi}?1002h{Csi}?1003h");

        Assert.Equal(MouseTracking.AnyEvent, engine.State.Modes.MouseTracking);
    }

    [Fact]
    public void MouseEncoding_TracksSgrExtendedMode()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1006h");
        Assert.Equal(MouseEncoding.Sgr, engine.State.Modes.MouseEncoding);

        engine.Feed($"{Csi}?1006l");
        Assert.Equal(MouseEncoding.X10, engine.State.Modes.MouseEncoding);
    }

    [Fact]
    public void MouseTrackingAndEncoding_AreIndependent()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1002h{Csi}?1006h{Csi}?1002l");

        Assert.Equal(MouseTracking.Off, engine.State.Modes.MouseTracking);
        Assert.Equal(MouseEncoding.Sgr, engine.State.Modes.MouseEncoding);
    }

    [Fact]
    public void ModesSetInOneSequence_AreAllReported()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?1000;1002;1003;1006;2004;1004h");

        var modes = engine.State.Modes;
        Assert.Equal(MouseTracking.AnyEvent, modes.MouseTracking);
        Assert.Equal(MouseEncoding.Sgr, modes.MouseEncoding);
        Assert.True(modes.BracketedPaste);
        Assert.True(modes.FocusReporting);
    }

    [Fact]
    public void SynchronizedOutput_TracksBeginAndEndOfFrame()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?2026h");
        Assert.True(
            engine.State.Modes.SynchronizedOutput,
            "?2026h begins a synchronized frame; the renderer must be able to see it to hold the previous image.");

        engine.Feed($"{Csi}?2026l");
        Assert.False(engine.State.Modes.SynchronizedOutput);
    }

    [Fact]
    public void SynchronizedOutput_CountsAFrameOnTheFeedThatClosesIt()
    {
        using var engine = EngineUnderTest.Create();

        var opened = engine.Feed($"{Csi}?2026hhalf drawn");
        var closed = engine.Feed($"{Csi}?2026l");

        Assert.True(opened.FramePending, "the program is mid-frame, so the renderer must not present yet");
        Assert.Equal(0, opened.FramesCompleted);
        Assert.False(closed.FramePending);
        Assert.Equal(1, closed.FramesCompleted);
    }

    [Fact]
    public void SynchronizedOutput_StillAppliesTheBytesInsideTheFrame()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}?2026habc{Csi}?2026l");

        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void UnknownPrivateMode_IsIgnoredWithoutDisturbingTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"a{Csi}?31337hb{Csi}?31337lc");

        Assert.Equal("abc", engine.RowText(0));
    }

    [Fact]
    public void ModeParameterIsNotPrinted()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 2);

        engine.Feed($"{Csi}?1049h{Csi}?2004h{Csi}?1006h");

        Assert.Equal(string.Empty, engine.RowText(0));
    }
}
