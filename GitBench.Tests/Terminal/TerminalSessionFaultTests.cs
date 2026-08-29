using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// What the pane does when the engine throws on the output it is handed. A drain runs on the thread
/// that owns the window, so an escaping exception takes the application down rather than the pane —
/// which is worth strictly less than a pane that says what went wrong and stops.
/// </summary>
public class TerminalSessionFaultTests
{
    [Fact]
    public void AnEngineThatThrowsOnOutput_FaultsThePaneInsteadOfTheApplication()
    {
        var reported = string.Empty;
        using var shell = new FaultingShell();
        shell.Session.Faulted += message => reported = message;

        shell.Feed("anything");

        Assert.Equal("the engine gave up", reported);
    }

    [Fact]
    public void AnEngineThatHasThrown_IsNotFedAgain()
    {
        using var shell = new FaultingShell();
        shell.Feed("first");

        shell.Feed("second");

        Assert.Equal(1, shell.Engine.Feeds);
    }

    [Fact]
    public void AFaultedSession_StillScrollsTheHistoryItAlreadyHas()
    {
        using var shell = new FaultingShell();
        shell.Feed("anything");

        Assert.False(shell.Session.Scroll(1), "There was no history behind a screen nothing reached.");
        Assert.Equal(0, shell.Session.ScrollOffset);
    }

    sealed class FaultingShell : IDisposable
    {
        readonly SeamPty _pty = new();
        readonly QueueDispatcher _dispatcher = new();

        public FaultingShell()
        {
            Engine = new ThrowingEngine();
            Session = TerminalSession.Start(
                () => _pty,
                new StubEngineFactory(Engine),
                new TerminalSize(20, 4),
                _dispatcher);
        }

        public TerminalSession Session { get; }

        public ThrowingEngine Engine { get; }

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

    sealed class StubEngineFactory(ITerminalEngine engine) : ITerminalEngineFactory
    {
        public ITerminalEngine Create(TerminalSetup setup) => engine;
    }

    /// <summary>An engine with the failure mode a vendored one has actually had: a screen state the
    /// next byte cannot be written to.</summary>
    sealed class ThrowingEngine : ITerminalEngine
    {
        public int Feeds { get; private set; }

        public ITerminalGrid Grid { get; } = new EmptyGrid();

        public TerminalState State => default;

        public FeedResult Feed(ReadOnlySpan<byte> bytes)
        {
            Feeds++;
            throw new NullReferenceException("the engine gave up");
        }

        public void Resize(TerminalSize size)
        {
        }

        public void Dispose()
        {
        }
    }

    sealed class EmptyGrid : ITerminalGrid
    {
        public TerminalSize Size => new(20, 4);

        public int ScrollbackRows => 0;

        public void CopyRow(int row, Span<TerminalCell> destination) => destination.Fill(TerminalCell.Blank);

        public bool ContinuesPreviousRow(int row) => false;
    }
}
