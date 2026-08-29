using static GitBench.Terminal.Vt.Tests.Vt;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// The cursor a program saves and asks for back, across a resize that happened in between. This is
/// the pane's own worst case: a full-screen program brackets its session in <c>?1049</c>, which
/// saves the shell's cursor on the way in and restores it on the way out, and the user resizes the
/// window while the program is up. A restore that lands off the screen is not a cosmetic error —
/// the next character printed is written to a row the buffer does not have.
/// </summary>
public class SavedCursorSpec
{
    [Fact]
    public void ACursorSavedBeforeTheScreenShrank_ComesBackOntoTheScreen()
    {
        using var engine = Shell(rows: 24);

        engine.Feed($"{Csi}?1049h");
        engine.Resize(new TerminalSize(80, 12));
        engine.Feed($"{Csi}?1049l");

        Assert.InRange(engine.State.Cursor.Row, 0, engine.Grid.Size.Rows - 1);
    }

    [Fact]
    public void PrintingAfterARestoreThatFollowedAShrink_ReachesTheGrid()
    {
        // The regression this spec exists for: an out-of-range restore threw out of the next Feed,
        // which in the pane is an exception on the thread that owns the screen.
        using var engine = Shell(rows: 24);

        engine.Feed($"{Csi}?1049h");
        engine.Resize(new TerminalSize(80, 12));
        engine.Feed($"{Csi}?1049l");
        engine.Feed("AFTER");

        Assert.Equal("PROMPT> AFTER", engine.RowText(engine.State.Cursor.Row).TrimEnd());
    }

    [Fact]
    public void ACursorSavedBeforeTheScreenGrew_ComesBackToItsOwnLine()
    {
        // Growing pulls history back onto the screen, so the line the cursor was on moves down it.
        using var engine = Shell(rows: 6);

        engine.Feed($"{Csi}?1049h");
        engine.Resize(new TerminalSize(80, 20));
        engine.Feed($"{Csi}?1049l");
        engine.Feed("AFTER");

        Assert.Equal("PROMPT> AFTER", engine.RowText(engine.State.Cursor.Row).TrimEnd());
    }

    [Fact]
    public void ACursorSavedOnALineWiderThanTheNewScreen_ComesBackOnIt()
    {
        using var engine = EngineUnderTest.Create(columns: 80, rows: 6);
        engine.Feed($"{Csi}1;60H");
        engine.Feed($"{Esc}7");

        engine.Resize(new TerminalSize(20, 6));
        engine.Feed($"{Esc}8");

        Assert.InRange(engine.State.Cursor.Column, 0, engine.Grid.Size.Columns - 1);
    }

    [Fact]
    public void SavingAndRestoringWithNoResizeInBetween_IsWhereItWasSaved()
    {
        using var engine = EngineUnderTest.Create(columns: 80, rows: 6);
        engine.Feed($"{Csi}3;10H{Esc}7");
        engine.Feed($"{Csi}6;1Hsomewhere else");

        engine.Feed($"{Esc}8");

        Assert.Equal((9, 2), engine.CursorAt());
    }

    /// <summary>A shell that has printed enough to fill its screen, with the cursor left at a prompt
    /// on the last line it wrote.</summary>
    static ITerminalEngine Shell(int rows)
    {
        var engine = EngineUnderTest.Create(columns: 80, rows: rows);

        for (var line = 0; line < 20; line++)
            engine.Feed($"normal line {line:D2}\r\n");
        engine.Feed("PROMPT> ");

        return engine;
    }
}
