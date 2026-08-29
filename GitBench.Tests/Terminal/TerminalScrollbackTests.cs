using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Where the pane is looking in the shell's history: what the wheel and the page keys move, and what
/// output arriving underneath it does to that.
/// </summary>
/// <remarks>
/// <para>
/// Driven through a real <see cref="TerminalSession"/> over a pseudo-terminal the test feeds a chunk
/// at a time, because the rule worth pinning is about output arriving while the reader is somewhere
/// else — which a session handed all of its bytes at once can never exercise. The pseudo-terminal is
/// <see cref="SeamPty"/>, already written for the input suite for the same reason.
/// </para>
/// <para>
/// Escape characters are spelled as "\u001b" and never written literally: an escape in a source
/// literal is invisible in every diff and review that follows.
/// </para>
/// </remarks>
public class TerminalScrollbackTests
{
    const string Esc = "\u001b";
    const string Csi = Esc + "[";

    // A four-row screen over ten lines of history: shallow enough that a test fills it in a
    // sentence, deep enough that output arriving can move the viewport without emptying it.
    const int Rows = 4;
    const int History = 10;

    [Fact]
    public void ANewSession_IsFollowingTheShell()
    {
        using var shell = Shell();

        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void ScrollingBack_StopsAtTheOldestLineTheHistoryStillHolds()
    {
        using var shell = Shell();
        shell.Print(20);

        Assert.True(shell.Session.Scroll(1000));
        Assert.Equal(History, shell.Session.ScrollOffset);
    }

    [Fact]
    public void ScrollingForwards_StopsAtTheLiveScreen()
    {
        using var shell = Shell();
        shell.Print(20);
        shell.Session.Scroll(2);

        Assert.True(shell.Session.Scroll(-1000));
        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void AScrollWithNowhereToGo_SaysItDidNotMove()
    {
        // What a wheel event turns on: one that moved nothing has to bubble out to whatever scrolls
        // around the pane rather than dead-ending on a screen with no history behind it.
        using var shell = Shell();
        shell.Print(20);

        Assert.False(shell.Session.Scroll(-1));
    }

    [Fact]
    public void AScreenWithNoHistoryYet_HasNothingToScrollBackThrough()
    {
        using var shell = Shell();
        shell.Print(Rows);

        Assert.False(shell.Session.Scroll(1));
        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void WhileScrolledBack_OutputLeavesTheReaderOnTheLineTheyWereReading()
    {
        using var shell = Shell();
        shell.Print(20);
        shell.Session.Scroll(2);
        var reading = shell.TopRowText;

        shell.Print(3);

        Assert.Equal(reading, shell.TopRowText);
    }

    [Fact]
    public void WhileFollowingTheShell_OutputStaysFollowed()
    {
        using var shell = Shell();
        shell.Print(20);

        shell.Print(3);

        Assert.Equal(0, shell.Session.ScrollOffset);
        Assert.Equal("l19", shell.TopRowText);
    }

    [Fact]
    public void WhenOutputOutrunsTheHistory_TheReaderIsCarriedToItsOldestLine()
    {
        // The reader is on the oldest line there is and the output pushes it out of the history
        // altogether. There is nowhere honest to put the viewport but the top of what is left; the
        // alternative is an offset pointing off the grid, which is a throw out of the next draw.
        using var shell = Shell();
        shell.Print(20);
        shell.Session.Scroll(History);

        shell.Print(4);

        Assert.Equal(History, shell.Session.ScrollOffset);
        Assert.Equal(History, shell.Session.Grid.ScrollbackRows);
    }

    [Fact]
    public void TheAlternateScreen_HasNoHistoryToScrollThrough()
    {
        // A full-screen program's screen is the whole of it; scrolling one is that program's own
        // job, through the keys it reads.
        using var shell = Shell();
        shell.Print(20);
        shell.Feed($"{Csi}?1049h");

        Assert.False(shell.Session.Scroll(1));
        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void EnteringTheAlternateScreenWhileScrolledBack_ReturnsToTheLiveScreen()
    {
        using var shell = Shell();
        shell.Print(20);
        shell.Session.Scroll(2);

        shell.Feed($"{Csi}?1049h");

        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void APage_IsAScreenLessOneLineOfOverlap()
    {
        using var shell = Shell();
        shell.Print(20);

        Assert.True(shell.Session.ScrollPages(1));
        Assert.Equal(Rows - 1, shell.Session.ScrollOffset);
    }

    [Fact]
    public void ScrollingToTheBottom_ReturnsToTheLiveScreen()
    {
        using var shell = Shell();
        shell.Print(20);
        shell.Session.Scroll(2);

        Assert.True(shell.Session.ScrollToBottom());
        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    [Fact]
    public void ScrollingToTheBottomFromTheBottom_SaysItDidNotMove()
    {
        using var shell = Shell();
        shell.Print(20);

        Assert.False(shell.Session.ScrollToBottom());
    }

    static ShellUnderTest Shell() => new(Rows, History);

    /// <summary>
    /// A running session whose output the test writes a chunk at a time, pumping each one into the
    /// engine before the next is sent.
    /// </summary>
    sealed class ShellUnderTest : IDisposable
    {
        readonly SeamPty _pty = new();
        readonly QueueDispatcher _dispatcher = new();

        int _printed;

        public ShellUnderTest(int rows, int history)
        {
            Session = TerminalSession.Start(
                () => _pty,
                new XtermSharpEngineFactory(),
                new TerminalSize(20, rows),
                _dispatcher,
                scrollbackLines: history);
        }

        public TerminalSession Session { get; }

        /// <summary>The line at the top of what the pane would draw, wherever the viewport is.</summary>
        public string TopRowText => Session.Grid.RowText(-Session.ScrollOffset);

        /// <summary>Prints numbered lines, continuing the count from the last call.</summary>
        public void Print(int lines)
        {
            var text = new StringBuilder();
            for (var line = 0; line < lines; line++)
            {
                if (_printed > 0) text.Append("\r\n");
                text.Append($"l{_printed++}");
            }

            Feed(text.ToString());
        }

        public void Feed(string output)
        {
            _pty.Emit(output);
            Assert.True(_dispatcher.WaitForPost(TimeSpan.FromSeconds(5)), "The output never arrived.");
            _dispatcher.Pump();
        }

        public void Dispose()
        {
            Session.Dispose();
            _pty.Dispose();
        }
    }
}
