using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// DECSCUSR. claude sets the cursor style once per session; vim toggles blink with <c>?12</c>.
/// Shape and blinking are two independent fields on <see cref="TerminalCursor"/> rather than one
/// six-valued style, because the renderer draws the shape and the animation clock owns the blink.
/// </summary>
public class CursorShapeSpec
{
    [Fact]
    public void CursorShape_StartsAsABlinkingBlock()
    {
        using var engine = EngineUnderTest.Create();

        Assert.Equal(CursorShape.Block, engine.State.Cursor.Shape);
        Assert.True(engine.State.Cursor.Blinking);
    }

    [Theory]
    [InlineData(1, CursorShape.Block, true)]
    [InlineData(2, CursorShape.Block, false)]
    [InlineData(3, CursorShape.Underline, true)]
    [InlineData(4, CursorShape.Underline, false)]
    [InlineData(5, CursorShape.Bar, true)]
    [InlineData(6, CursorShape.Bar, false)]
    public void SetCursorStyle_SelectsShapeAndBlinkIndependently(int parameter, CursorShape shape, bool blinking)
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}{parameter} q");

        Assert.Equal(shape, engine.State.Cursor.Shape);
        Assert.Equal(blinking, engine.State.Cursor.Blinking);
    }

    [Fact]
    public void SetCursorStyle_Zero_ReturnsToTheDefaultShape()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}6 q{Csi}0 q");

        Assert.Equal(CursorShape.Block, engine.State.Cursor.Shape);
    }

    [Fact]
    public void CursorBlink_TracksTheDecPrivateMode()
    {
        using var engine = EngineUnderTest.Create();

        engine.Feed($"{Csi}?12l");
        Assert.False(engine.State.Cursor.Blinking);

        engine.Feed($"{Csi}?12h");
        Assert.True(engine.State.Cursor.Blinking);
    }

    [Fact]
    public void SetCursorStyle_DoesNotPrintItsParameterToTheGrid()
    {
        using var engine = EngineUnderTest.Create(columns: 10, rows: 2);

        engine.Feed($"{Csi}2 q");

        Assert.Equal(string.Empty, engine.RowText(0));
    }
}
