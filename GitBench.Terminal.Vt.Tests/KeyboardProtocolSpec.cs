using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Kitty keyboard progressive enhancement and its modifyOtherKeys fallback — the negotiation that
/// decides whether Shift+Enter works in claude without a <c>/terminal-setup</c> step. The claude
/// corpus contains four <c>CSI &lt; u</c> pops and four <c>CSI &gt; 4 ; n m</c>, and the ConPTY
/// spike confirmed both reach us unintercepted, so implementing them is entirely ours.
/// </summary>
/// <remarks>
/// The sharpest requirement here is negative: <c>CSI u</c> with a private prefix is a keyboard
/// sequence, not the ancient SCO "restore cursor". An engine that dispatches on the final byte
/// alone will move the cursor every time claude pops its keyboard flags.
/// </remarks>
public class KeyboardProtocolSpec
{
    [Fact]
    public void KeyboardFlags_StartAtLegacyEncoding()
    {
        using var engine = EngineUnderTest.Create();

        Assert.Equal(0, engine.State.Modes.KeyboardProtocolFlags);
    }

    [Fact]
    public void PushingKeyboardFlags_MakesThemCurrent()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}>1u");

        Assert.Equal(1, engine.State.Modes.KeyboardProtocolFlags);
    }

    [Fact]
    public void PoppingKeyboardFlags_ReturnsToThePreviousLevel()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}>1u{Csi}<u");

        Assert.Equal(0, engine.State.Modes.KeyboardProtocolFlags);
    }

    [Fact]
    public void PushingKeyboardFlags_DoesNotMoveTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 4);

        engine.Feed($"{Csi}3;5H{Csi}>1u");

        Assert.Equal((4, 2), engine.CursorAt());
    }

    [Fact]
    public void PoppingKeyboardFlags_DoesNotMoveTheCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 4);

        engine.Feed($"{Csi}3;5H{Csi}<u");

        Assert.Equal((4, 2), engine.CursorAt());
    }

    [Fact]
    public void QueryingKeyboardFlags_AnswersWithTheCurrentFlags()
    {
        using var engine = EngineUnderTest.Create();

        var result = engine.Feed($"{Csi}?u");

        Assert.Equal($"{Csi}?0u", result.Text());
    }

    [Fact]
    public void QueryingKeyboardFlags_AfterAPush_ReportsThePushedFlags()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}>5u");
        var result = engine.Feed($"{Csi}?u");

        Assert.Equal($"{Csi}?5u", result.Text());
    }

    [Fact]
    public void ModifyOtherKeys_RecordsTheRequestedLevel()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}>4;2m");

        Assert.Equal(2, engine.State.Modes.ModifyOtherKeys);
    }

    [Fact]
    public void ModifyOtherKeys_WithNoLevel_TurnsTheFallbackOff()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}>4;2m{Csi}>4m");

        Assert.Equal(0, engine.State.Modes.ModifyOtherKeys);
    }

    [Fact]
    public void ModifyOtherKeys_DoesNotChangeCellAttributes()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}>4;2mX");

        Assert.Equal(CellAttributes.None, engine.CellAt(0, 0).Attributes);
        Assert.Equal(TerminalColor.Default, engine.CellAt(0, 0).Foreground);
    }

    [Fact]
    public void RestoreCursor_WithoutAPrivatePrefix_StillMeansRestoreCursor()
    {
        using var engine = EngineUnderTest.Create(columns: 20, rows: 4);

        engine.Feed($"{Csi}2;3H{Csi}s{Csi}4;1H{Csi}u");

        Assert.Equal((2, 1), engine.CursorAt());
    }
}
