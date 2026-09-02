using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// What happens to a selection while the shell keeps printing under it.
/// </summary>
/// <remarks>
/// The selection lives on the session for the reason the scroll offset does — output moves the text
/// it covers, and the session is the only thing that sees output arrive — so these are the tests
/// that say the arithmetic is applied at all, and applied only where it means anything.
/// </remarks>
public class TerminalSelectionLifetimeTests
{
    const string Csi = "\u001b[";

    [Fact]
    public void Initially_NothingIsSelected()
    {
        using var run = TerminalRun.Started();

        Assert.Null(run.Vm.Selection);
    }

    [Fact]
    public void OutputThatScrollsTheScreen_CarriesTheSelectionWithTheTextItCovers()
    {
        using var run = TerminalRun.Started();

        // The screen has to be full before anything can leave the top of it, which is the only way
        // a line ever moves into the history.
        FillScreen(run);
        var selected = run.RowText(0);
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(1, 0), SelectionGranularity.Character);
        Assert.Equal(selected, run.Vm.SelectionText());

        Emit(run, "tail1\r\ntail2\r\ntail3\r\n", () => Shows(run, "tail3"));

        // Three lines arrived under it, so the row it was on is three rows further back — and it
        // still names the same characters.
        Assert.Equal(-3, run.Vm.Selection?.Start.Row);
        Assert.Equal(selected, run.Vm.SelectionText());
    }

    /// <remarks>
    /// The reason to select everything is to take away what a command printed, and by the time it
    /// has finished printing most of that has usually left the visible screen.
    /// </remarks>
    [Fact]
    public void SelectingEverything_TakesTheHistoryAndNotJustTheVisibleScreen()
    {
        using var run = TerminalRun.Started();
        FillScreen(run);
        var oldest = run.RowText(0);
        Emit(run, "tail1\r\ntail2\r\ntail3\r\n", () => Shows(run, "tail3"));

        Assert.True(run.Vm.SelectAll());

        // The first row of the history rather than the first row of the screen, whatever depth the
        // history has reached by now.
        Assert.Equal(-run.Session!.Grid.ScrollbackRows, run.Vm.Selection?.Start.Row);
        Assert.Equal(0, run.Vm.Selection?.Start.Column);
        Assert.Contains(oldest, run.Vm.SelectionText());
        Assert.Contains("tail3", run.Vm.SelectionText());
    }

    /// <remarks>
    /// The rows below the prompt are part of the screen but not part of anything anyone means by
    /// "all". A shell sitting idle near the top of a tall pane would otherwise put a screen's worth
    /// of blank lines on the clipboard, and pasting those is a screen's worth of pressing Enter.
    /// </remarks>
    [Fact]
    public void SelectingEverything_StopsAtTheLastRowHoldingAnything()
    {
        using var run = TerminalRun.Started();
        Emit(run, "one\r\ntwo\r\nthree", () => Shows(run, "three"));

        run.Vm.SelectAll();

        Assert.Equal("one\ntwo\nthree", run.Vm.SelectionText());
        Assert.Equal(2, run.Vm.Selection?.End.Row);
    }

    [Fact]
    public void SelectingEverything_OnAScreenHoldingNothing_SelectsNothing()
    {
        using var run = TerminalRun.Started();

        Assert.False(run.Vm.SelectAll());
        Assert.Null(run.Vm.Selection);
    }

    [Fact]
    public void SelectingEverythingTwice_ChangesNothingTheSecondTime()
    {
        using var run = TerminalRun.Started();
        FillScreen(run);

        Assert.True(run.Vm.SelectAll());
        Assert.False(run.Vm.SelectAll());
    }

    [Fact]
    public void ASelectionScrolledOutOfTheHistory_IsDropped()
    {
        using var run = TerminalRun.ShallowHistory();
        FillScreen(run);
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(1, 0), SelectionGranularity.Character);

        // Far more lines than the five this terminal keeps, so the row it was on is long gone.
        Emit(
            run,
            string.Concat(Enumerable.Range(0, 80).Select(i => $"line{i}\r\n")) + "done\r\n",
            () => Shows(run, "done"));

        Assert.Null(run.Vm.Selection);
        Assert.Equal(string.Empty, run.Vm.SelectionText());
    }

    /// <remarks>
    /// The alternate screen has no history, so a selection cannot be carried into one — and the
    /// scroll counter keeps counting there, so carrying by it would walk the selection off a grid
    /// whose only rows are the visible ones.
    /// </remarks>
    [Fact]
    public void EnteringTheAlternateScreen_ClearsTheSelection()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("text\r\n");
        run.WaitFor(() => run.RowText(0) == "text", "the line to select");
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(3, 0), SelectionGranularity.Character);

        Emit(run, $"{Csi}?1049h", () => run.Session?.State.Modes.AlternateScreen == true);

        Assert.Null(run.Vm.Selection);
    }

    [Fact]
    public void LeavingTheAlternateScreen_ClearsWhateverWasSelectedOnIt()
    {
        using var run = TerminalRun.Started();
        Emit(run, $"{Csi}?1049hfull screen", () => run.RowText(0) == "full screen");
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(3, 0), SelectionGranularity.Character);

        Emit(run, $"{Csi}?1049l", () => run.Session?.State.Modes.AlternateScreen == false);

        Assert.Null(run.Vm.Selection);
    }

    /// <remarks>
    /// A full-screen program prints inside a scroll region, where no line leaves the top of the
    /// screen and the counter does not move. Shifting by it there would drag the selection up the
    /// screen while the text stayed put.
    /// </remarks>
    [Fact]
    public void OnTheAlternateScreen_TheSelectionStaysWhereItWasPutRatherThanBeingCarried()
    {
        using var run = TerminalRun.Started();
        Emit(run, $"{Csi}?1049h", () => run.Session?.State.Modes.AlternateScreen == true);
        run.Vm.Select(new GridPoint(0, 2), new GridPoint(3, 2), SelectionGranularity.Character);

        Emit(run, "aaaa\r\nbbbb\r\ncccc\r\n", () => run.RowText(2) == "cccc");

        Assert.Equal(2, run.Vm.Selection?.Start.Row);
    }

    [Fact]
    public void AResize_ClearsTheSelectionBecauseTheReflowMovesEverything()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("text\r\n");
        run.WaitFor(() => run.RowText(0) == "text", "the line to select");
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(3, 0), SelectionGranularity.Character);

        run.Vm.ReportViewport(new TerminalSize(60, 20));

        Assert.Null(run.Vm.Selection);
    }

    [Fact]
    public void Typing_ClearsTheSelection()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("text\r\n");
        run.WaitFor(() => run.RowText(0) == "text", "the line to select");
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(3, 0), SelectionGranularity.Character);

        run.Vm.SendInput("x"u8);

        Assert.Null(run.Vm.Selection);
    }

    [Fact]
    public void AMouseReport_DoesNotClearTheSelection()
    {
        using var run = TerminalRun.Started();
        run.Pty.Emit("text\r\n");
        run.WaitFor(() => run.RowText(0) == "text", "the line to select");
        run.Vm.Select(new GridPoint(0, 0), new GridPoint(3, 0), SelectionGranularity.Character);

        run.Vm.SendMouse("\u001b[M x"u8);

        Assert.NotNull(run.Vm.Selection);
    }

    /// <summary>Pushes output and waits for the drain that applies it, keyed on what it puts on screen.</summary>
    static void Emit(TerminalRun run, string output, Func<bool> applied)
    {
        run.Pty.Emit(output);
        run.WaitFor(applied, "the output to be applied");
    }

    /// <summary>Prints a full screen, so that the next line printed pushes one into the history.</summary>
    static void FillScreen(TerminalRun run)
    {
        var rows = run.Session?.Grid.Size.Rows ?? 24;
        var lines = string.Concat(Enumerable.Range(0, rows).Select(i => $"l{i}\r\n"));

        Emit(run, lines, () => Shows(run, $"l{rows - 1}"));
    }

    static bool Shows(TerminalRun run, string text)
    {
        var rows = run.Session?.Grid.Size.Rows ?? 0;

        for (var row = 0; row < rows; row++)
            if (run.RowText(row) == text)
                return true;

        return false;
    }
}
