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
        // Two coloured cells, then the rest of the row in the default style. A run is a span of one
        // style, not a span of text, so this row is exactly two of them - and the second begins at
        // the column the style changed on rather than at the row's left edge.
        var (harness, _) = Draw(Csi("48;2;10;20;30m", "ab"));

        var runs = harness.Canvas.GlyphRuns;
        Assert.Equal("ab", runs[0].Text);
        Assert.Equal(0f, runs[0].Origin.X);
        Assert.Equal(2 * Advance, runs[1].Origin.X);
    }

    [Fact]
    public void ATruecolourBackground_IsPaintedBehindItsRun()
    {
        // Asserted through the background rather than the foreground on purpose: DrawGlyphRun takes
        // the TextStyle by reference and the view reuses one instance across runs (as DiffRowPainter
        // does), so a recorded run's colour is whichever one was drawn last. A rect's style is
        // allocated per call, so this one is the run's own.
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
