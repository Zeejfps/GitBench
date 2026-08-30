using System.Collections.Concurrent;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// The terminal renderer, driven headlessly through the real chain a pane uses: recorded bytes into
/// a <see cref="TerminalSession"/>, its engine's grid into <see cref="TerminalGridView"/>, and the
/// draw calls that come out. The synthetic measurer makes a cell exactly 8x16, so a column's
/// position is a number the test can state rather than approximate.
/// </summary>
public class TerminalGridViewTests
{
    private const float Advance = 8f;
    private const float CellHeight = 16f;
    private const int Width = 800;
    private const int Height = 600;

    // 800/8 and 600/16: what an 800x600 pane is worth in cells.
    private const int ExpectedColumns = 100;
    private const int ExpectedRows = 37;

    [Fact]
    public void AScreensText_ReachesTheCanvasAsGlyphRuns()
    {
        var (harness, _) = Draw(Vt("hello"));

        Assert.Contains(harness.Canvas.GlyphRuns, run => run.Text.StartsWith("hello"));
    }

    [Fact]
    public void EachRowIsDrawnAtItsOwnLine_TopDown()
    {
        var (harness, _) = Draw(Vt("first\r\nsecond"));

        var first = Row(harness, "first");
        var second = Row(harness, "second");

        Assert.Equal(Height, first.Origin.Y);
        Assert.Equal(Height - CellHeight, second.Origin.Y);
        Assert.Equal(0f, first.Origin.X);
        Assert.Equal(Advance, first.CellAdvance);
    }

    [Fact]
    public void AStyleChange_EndsARunAndStartsTheNextAtThatColumn()
    {
        // Two coloured cells and then two plain ones. A run is a span of one style, not a span of
        // text, so this row is exactly two of them - and the second begins at the column the style
        // changed on rather than at the row's left edge.
        //
        // Both halves carry text on purpose. A row is padded to the width of the screen in whatever
        // style was in force, so a row that ends on a style change ends in a run that is entirely
        // blank - and those the view drops rather than asking the canvas for a glyph per column that
        // it then draws nothing for.
        var (harness, _) = Draw(Csi("48;2;10;20;30m", "ab" + Esc + "[0mcd"));

        var runs = harness.Canvas.GlyphRuns;
        Assert.Equal(2, runs.Count);
        Assert.Equal("ab", runs[0].Text);
        Assert.Equal(0f, runs[0].Origin.X);
        Assert.Equal("cd", runs[1].Text);
        Assert.Equal(2 * Advance, runs[1].Origin.X);
    }

    [Fact]
    public void TrailingBlanks_AreNotAskedOfTheCanvas()
    {
        // The screen is mostly empty and every blank column would otherwise cost a glyph lookup that
        // draws nothing, so only the columns carrying text are handed over. The background is a
        // rectangle drawn to the run's full width, and is unaffected.
        var (harness, _) = Draw(Vt("hi"));

        Assert.Equal("hi", Assert.Single(harness.Canvas.GlyphRuns).Text);
    }

    [Fact]
    public void ATruecolourBackground_IsPaintedBehindItsRun()
    {
        // Asserted through the background rather than the foreground on purpose: it is the one
        // colour a row states once per run rather than once per glyph. The view hands the same style
        // object to every call it makes - a canvas is not allowed to hold on to one, and the
        // recording canvas takes its copy at the call - so what is read back here is the style as
        // this rectangle was drawn, not as the last row left it.
        var (harness, _) = Draw(Csi("48;2;10;20;30m", "X"));

        Assert.Contains(
            harness.Canvas.Rects,
            r => r.Inputs.Style.BackgroundColor == 0xFF0A141Eu
                && r.Inputs.Position.Width == Advance
                && r.Inputs.Position.Height == CellHeight);
    }

    [Fact]
    public void ScrolledBack_TheHistoryIsDrawnAboveTheLiveScreen()
    {
        // Fifty lines into a thirty-seven-row pane: rows 0 to 36 hold l13 to l49 and the thirteen
        // before them are history. One line back puts l12 at the top and pushes l49 off the bottom.
        var (harness, _) = Draw(Lines(50), session => session.Scroll(1));

        Assert.Equal(Height, Row(harness, "l12").Origin.Y);
        Assert.Equal(Height - 36 * CellHeight, Row(harness, "l48").Origin.Y);
        Assert.DoesNotContain(harness.Canvas.GlyphRuns, run => run.Text.StartsWith("l49"));
    }

    [Fact]
    public void FollowingTheShell_TheLiveScreenStartsAtTheTop()
    {
        var (harness, _) = Draw(Lines(50));

        Assert.Equal(Height, Row(harness, "l13").Origin.Y);
        Assert.Contains(harness.Canvas.GlyphRuns, run => run.Text.StartsWith("l49"));
    }

    [Fact]
    public void ScrolledPastTheCursor_TheCursorIsNotDrawn()
    {
        // The cursor is on the live screen, which one line of scrollback pushes off the bottom of
        // the pane. Drawing it clamped to the last row would have it claiming a position the shell
        // is not at, on a line the shell did not write.
        var (scrolled, _) = Draw(Lines(50), session => session.Scroll(1));
        var (following, _) = Draw(Lines(50));

        Assert.DoesNotContain(scrolled.Canvas.Rects, IsTheCursor);
        Assert.Contains(following.Canvas.Rects, IsTheCursor);
    }

    [Fact]
    public void TheViewportIsReportedInCells_NotPixels()
    {
        var (_, reported) = Draw(Vt("x"));

        Assert.Equal(new TerminalSize(ExpectedColumns, ExpectedRows), reported);
    }

    [Fact]
    public void BeforeAShellExists_TheStartingMessageIsDrawn()
    {
        using var harness = Harness(view => view.StartingMessage = "Starting shell…");

        var canvas = harness.Render();

        Assert.Contains(canvas.Texts, t => t.Inputs.Text == "Starting shell…");
        Assert.Empty(canvas.GlyphRuns);
    }

    [Fact]
    public void AnIdleTerminal_DrawsNothingButItsBackground()
    {
        // The pane's start gate is what an idle terminal shows, and it is a widget over this view
        // rather than a message inside it. A "Starting shell…" behind it would be a lie.
        using var harness = Harness(view =>
        {
            view.StartingMessage = "Starting shell…";
            view.SetRenderState(new TerminalRenderState.Idle());
        });

        var canvas = harness.Render();

        Assert.Empty(canvas.Texts);
        Assert.Empty(canvas.GlyphRuns);
    }

    [Fact]
    public void AnExitedShell_StillHasItsScreenAndItsCells()
    {
        // What a finished command printed is what a reader wants to scroll and point at, so an exit
        // must not blank the pane or take its geometry away.
        var (harness, view) = Ended(Vt("hello"), session => new TerminalRenderState.Exited(session));

        Assert.Contains(harness.Canvas.GlyphRuns, run => run.Text.StartsWith("hello"));
        Assert.True(view.TryLocate(new PointF(Advance + 2f, Height - 4f), out _, out _));
    }

    [Fact]
    public void AFaultedShell_KeepsTheScreenItHadPrinted()
    {
        var (harness, _) = Ended(
            Vt("hello"), session => new TerminalRenderState.Faulted(session, "the reader failed"));

        Assert.Contains(harness.Canvas.GlyphRuns, run => run.Text.StartsWith("hello"));
    }

    [Fact]
    public void AStateWithNoDrawing_ThrowsRatherThanDrawingSomethingElse()
    {
        // The states are the whole of what this view does, so a new one has to arrive as a build
        // break or a loud failure - never as a pane quietly showing the wrong thing.
        using var harness = Harness(view => view.SetRenderState(new UnknownState()));

        Assert.Throws<NotSupportedException>(() => harness.Render());
    }

    [Fact]
    public void AFailedStart_DrawsItsMessageInsteadOfAScreen()
    {
        using var harness = Harness(view =>
            view.SetRenderState(new TerminalRenderState.Failed("no shell here")));

        var canvas = harness.Render();

        Assert.Contains(canvas.Texts, t => t.Inputs.Text == "no shell here");
        Assert.Empty(canvas.GlyphRuns);
    }

    [Fact]
    public void ADrawnPane_NamesTheCellUnderAPoint()
    {
        var (_, view) = Located(Vt("x"));

        Assert.True(view.TryLocate(new PointF(3 * Advance + 4f, Height - 2 * CellHeight - 8f), out var column, out var row));
        Assert.Equal(3, column);
        Assert.Equal(2, row);
    }

    [Fact]
    public void APaneThatHasNotBeenDrawn_HasNoCells()
    {
        using var harness = Harness(view => view.StartingMessage = "Starting shell…");
        var view = (TerminalGridView)harness.Root;

        Assert.False(view.TryLocate(new PointF(10f, 10f), out _, out _));
    }

    [Fact]
    public void ScrolledBack_APointOverTheHistoryIsNotACellOfTheLiveScreen()
    {
        var (_, view) = Located(Lines(50), session => session.Scroll(1));

        Assert.False(view.TryLocate(new PointF(0f, Height - 4f), out _, out _));
    }

    [Fact]
    public void ScrolledBack_TheCellsBelowTheHistoryAreTheLiveScreensOwn()
    {
        var (_, view) = Located(Lines(50), session => session.Scroll(1));

        Assert.True(view.TryLocate(new PointF(0f, Height - CellHeight - 4f), out _, out var row));
        Assert.Equal(0, row);
    }

    [Fact]
    public void APointOutsideThePane_HasNoCell()
    {
        var (_, view) = Located(Vt("x"));

        Assert.False(view.TryLocate(new PointF(-1f, Height / 2f), out _, out _));
    }

    private static RecordedGlyphRun Row(GuiTestHarness harness, string text) =>
        harness.Canvas.GlyphRuns.First(r => r.Text.StartsWith(text));

    /// <summary>Numbered lines, so a row's identity is readable in a failure message.</summary>
    private static byte[] Lines(int count) =>
        Vt(string.Join("\r\n", Enumerable.Range(0, count).Select(line => $"l{line}")));

    /// <summary>
    /// The cursor is the one rectangle painted in the cursor colour: a cell's background rectangle
    /// is only drawn when it differs from the pane's, and no cell here carries one at all.
    /// </summary>
    private static bool IsTheCursor(RecordedRect rect) =>
        rect.Inputs.Style.BackgroundColor == ThemeStyles.Dark.Terminal.Cursor;

    /// <summary>The escape character, named for the same reason <see cref="Csi"/> spells one out.</summary>
    private const string Esc = "\u001b";

    private static byte[] Vt(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A CSI sequence and the text after it, spelled out rather than embedded: an
    /// escape character in a source literal is invisible in every diff and review that follows.</summary>
    private static byte[] Csi(string sequence, string text) =>
        Encoding.UTF8.GetBytes("\u001b[" + sequence + text);

    /// <summary>
    /// Feeds <paramref name="output"/> through a session and draws the screen it produces, handing
    /// back the canvas that captured it and the viewport the view reported.
    /// </summary>
    private static (GuiTestHarness Harness, TerminalSize? Reported) Draw(
        byte[] output,
        Action<TerminalSession>? scrolled = null)
    {
        var dispatcher = new QueueDispatcher();
        using var session = TerminalSession.Start(
            () => new RecordedPtySession(output),
            new XtermSharpEngineFactory(),
            new TerminalSize(ExpectedColumns, ExpectedRows),
            dispatcher);

        // The recording ends its stream once its bytes are gone, so the session having exited means
        // every batch has been posted — and pumping now feeds all of them.
        Assert.True(session.Exited.Wait(TimeSpan.FromSeconds(5)), "The recording never finished.");
        dispatcher.Pump();

        scrolled?.Invoke(session);

        TerminalSize? reported = null;
        var harness = Harness(view =>
        {
            view.OnViewportChanged = size => reported = size;
            view.SetRenderState(new TerminalRenderState.Running(session));
        });

        harness.Render();
        return (harness, reported);
    }

    private static (GuiTestHarness Harness, TerminalGridView View) Located(
        byte[] output,
        Action<TerminalSession>? scrolled = null)
    {
        var dispatcher = new QueueDispatcher();
        var session = TerminalSession.Start(
            () => new RecordedPtySession(output),
            new XtermSharpEngineFactory(),
            new TerminalSize(ExpectedColumns, ExpectedRows),
            dispatcher);

        Assert.True(session.Exited.Wait(TimeSpan.FromSeconds(5)), "The recording never finished.");
        dispatcher.Pump();

        scrolled?.Invoke(session);

        var harness = Harness(view => view.SetRenderState(new TerminalRenderState.Running(session)));
        harness.Render();
        return (harness, (TerminalGridView)harness.Root);
    }

    /// <summary>Draws a session that has finished, in whichever state a caller says it ended in.</summary>
    private static (GuiTestHarness Harness, TerminalGridView View) Ended(
        byte[] output,
        Func<TerminalSession, TerminalRenderState> ended)
    {
        var dispatcher = new QueueDispatcher();
        var session = TerminalSession.Start(
            () => new RecordedPtySession(output),
            new XtermSharpEngineFactory(),
            new TerminalSize(ExpectedColumns, ExpectedRows),
            dispatcher);

        Assert.True(session.Exited.Wait(TimeSpan.FromSeconds(5)), "The recording never finished.");
        dispatcher.Pump();

        var harness = Harness(view => view.SetRenderState(ended(session)));
        harness.Render();
        return (harness, (TerminalGridView)harness.Root);
    }

    /// <summary>A state this view has no drawing for, which is the point.</summary>
    private sealed record UnknownState : TerminalRenderState;

    private static GuiTestHarness Harness(Action<TerminalGridView> configure) =>
        GuiTestHarness.Create(
            ctx =>
            {
                var view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                configure(view);
                return view;
            },
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            });

    /// <summary>
    /// Collects posted work instead of running it, so a test says when the engine is fed rather than
    /// racing the reader thread for it.
    /// </summary>
    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Pump()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }
    }
}
