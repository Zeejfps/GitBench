using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Synchronized output (<c>?2026</c>), honoured where the plan put it: the engine applies every byte
/// as it arrives, the session says whether a frame is open, and the pane keeps drawing the last
/// complete screen until it closes — or until it has been open long enough to be presumed dead.
/// </summary>
public class TerminalSynchronizedOutputTests
{
    private const float Advance = 8f;
    private const int Width = 800;
    private const int Height = 600;

    private static readonly TimeSpan PostPatience = TimeSpan.FromSeconds(5);

    private const string Esc = "";
    private const string BeginFrame = Esc + "[?2026h";
    private const string EndFrame = Esc + "[?2026l";
    private const string Home = Esc + "[H";

    [Fact]
    public void AFrameIsHeld_FromItsOpeningUntilItsClose()
    {
        using var terminal = Terminal.Start();

        terminal.Program("hello");
        Assert.False(terminal.Session.IsHoldingFrame);

        terminal.Program(BeginFrame + Home + "world");
        Assert.True(terminal.Session.IsHoldingFrame);

        terminal.Program(EndFrame);
        Assert.False(terminal.Session.IsHoldingFrame);
    }

    [Fact]
    public void AFrameLeftOpen_StopsBeingHeldOnceItHasOverstayed()
    {
        var time = new ManualTimeProvider();
        using var terminal = Terminal.Start(time);

        terminal.Program(BeginFrame + "half");
        Assert.True(terminal.Session.IsHoldingFrame);

        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(terminal.Session.IsHoldingFrame);

        // More output with the frame still open does not restart the clock: the program has had its
        // chance, and the screen stays live until it closes the frame and opens another.
        terminal.Program("-drawn");
        Assert.False(terminal.Session.IsHoldingFrame);
    }

    [Fact]
    public void WhileAFrameIsOpen_ThePaneShowsTheLastCompleteScreen()
    {
        using var terminal = Terminal.Start();
        terminal.Program("hello");

        using var harness = Harness(terminal.Session);
        harness.Render();
        Assert.Equal("hello", Assert.Single(harness.Canvas.GlyphRuns).Text);

        // The frame moves the cursor home and starts overwriting, which is exactly the half-drawn
        // state a reader must not see.
        terminal.Program(BeginFrame + Home + "world");
        harness.Render();

        Assert.Equal("hello", Assert.Single(harness.Canvas.GlyphRuns).Text);
        Assert.Equal(5 * Advance, Assert.Single(harness.Canvas.Rects, IsTheCursor).Inputs.Position.Left);

        terminal.Program(EndFrame);
        harness.Render();

        Assert.Equal("world", Assert.Single(harness.Canvas.GlyphRuns).Text);
        Assert.Equal(5 * Advance, Assert.Single(harness.Canvas.Rects, IsTheCursor).Inputs.Position.Left);
    }

    [Fact]
    public void AFrameThatOverstays_IsShownAsItIs()
    {
        var time = new ManualTimeProvider();
        using var terminal = Terminal.Start(time);
        terminal.Program("hello");

        using var harness = Harness(terminal.Session);
        harness.Render();

        terminal.Program(BeginFrame + Home + "world");
        time.Advance(TimeSpan.FromMilliseconds(200));
        harness.Render();

        Assert.Equal("world", Assert.Single(harness.Canvas.GlyphRuns).Text);
    }

    [Fact]
    public void AFrameOpenBeforeAnythingWasDrawn_IsShownLive()
    {
        // There is no earlier image to prefer, and a blank pane until the first frame closes would
        // read as a shell that has not started.
        using var terminal = Terminal.Start();
        terminal.Program(BeginFrame + "first");

        using var harness = Harness(terminal.Session);
        harness.Render();

        Assert.Equal("first", Assert.Single(harness.Canvas.GlyphRuns).Text);
    }

    private static bool IsTheCursor(RecordedRect rect) =>
        rect.Inputs.Style.BackgroundColor == ThemeStyles.Dark.Terminal.Cursor;

    private static GuiTestHarness Harness(TerminalSession session) =>
        GuiTestHarness.Create(
            ctx =>
            {
                var view = new TerminalGridView(ctx.Require<IThemeService<ThemeStyles>>());
                view.SetRenderState(new TerminalRenderState.Running(session));
                return view;
            },
            width: Width,
            height: Height,
            configure: TerminalTestHost.Configure);

    /// <summary>
    /// A session over a scripted terminal, with the program's output pumped through to the engine
    /// synchronously so a test reads the screen exactly one batch at a time.
    /// </summary>
    private sealed class Terminal : IDisposable
    {
        private readonly SeamPty _pty;
        private readonly QueuedDispatcher _dispatcher;

        private Terminal(SeamPty pty, QueuedDispatcher dispatcher, TerminalSession session)
        {
            _pty = pty;
            _dispatcher = dispatcher;
            Session = session;
        }

        public TerminalSession Session { get; }

        public static Terminal Start(TimeProvider? time = null)
        {
            var pty = new SeamPty();
            var dispatcher = new QueuedDispatcher();
            var session = TerminalSession.Start(
                () => pty,
                new XtermSharpEngineFactory(),
                new TerminalSize(100, 37),
                dispatcher,
                time: time);

            return new Terminal(pty, dispatcher, session);
        }

        /// <summary>The program writes <paramref name="vt"/>, and the session takes it.</summary>
        public void Program(string vt)
        {
            _pty.Emit(vt);
            Assert.True(_dispatcher.WaitForPost(PostPatience), "The reader never posted the batch.");
            _dispatcher.Drain();
        }

        public void Dispose() => Session.Dispose();
    }
}
