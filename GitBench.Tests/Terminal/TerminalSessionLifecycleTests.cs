using System.Collections.Concurrent;
using System.Text;
using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// A terminal's own state machine: when it has a shell, when it does not, and what starting one
/// costs. The point of the whole feature is that drawing a pane no longer spawns anything, so most
/// of these are assertions that nothing happened.
/// </summary>
public class TerminalInstanceLifecycleTests
{
    static readonly TerminalSize Viewport = new(100, 37);

    [Fact]
    public void AFreshTerminal_IsIdleAndHasStartedNothing()
    {
        var launch = new CountingLaunch();
        using var terminal = new TerminalInstance(launch, new QueuedDispatcher());

        Assert.IsType<TerminalRenderState.Idle>(terminal.Render.Value);
        Assert.Equal(0, launch.Starts);
        Assert.False(terminal.IsAcceptingInput);
        Assert.True(terminal.CanStart);
    }

    [Fact]
    public void ReportingAViewport_StartsNothingOnItsOwn()
    {
        // The whole gate: a pane that is drawn is not a pane that asked for a shell.
        var launch = new CountingLaunch();
        using var terminal = new TerminalInstance(launch, new QueuedDispatcher());

        terminal.ReportViewport(Viewport);

        Assert.Equal(0, launch.Starts);
        Assert.IsType<TerminalRenderState.Idle>(terminal.Render.Value);
    }

    [Fact]
    public void StartingAfterAViewport_SpawnsAtTheSizeThePaneReported()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        terminal.ReportViewport(Viewport);

        terminal.Start();

        Running(terminal, dispatcher);
        Assert.Equal(1, launch.Starts);
        Assert.Equal(Viewport, launch.StartedAt);
    }

    [Fact]
    public void StartingBeforeAnySize_WaitsForTheViewportRatherThanGuessing()
    {
        // A shell spawned at a guessed size draws its prompt at the wrong width and then redraws the
        // whole buffer on the resize that follows, so the first thing on screen is a mistake.
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);

        terminal.Start();

        Assert.IsType<TerminalRenderState.Starting>(terminal.Render.Value);
        Assert.Equal(0, launch.Starts);

        terminal.ReportViewport(Viewport);

        Running(terminal, dispatcher);
        Assert.Equal(Viewport, launch.StartedAt);
    }

    [Fact]
    public void StartingTwiceWhileStarting_SpawnsOneShell()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        terminal.ReportViewport(Viewport);

        terminal.Start();
        terminal.Start();

        Running(terminal, dispatcher);
        Assert.Equal(1, launch.Starts);
    }

    [Fact]
    public void StartingWhileAShellIsRunning_DoesNothing()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        var session = Start(terminal, launch, dispatcher);

        terminal.Start();
        dispatcher.Drain();

        Assert.Equal(1, launch.Starts);
        Assert.Same(session, Session(terminal));
    }

    [Fact]
    public void WhenTheShellExits_TheScreenIsKeptAndInputStops()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        var session = Start(terminal, launch, dispatcher);

        launch.Pty.ShellExits();

        Pump.WaitFor(dispatcher, () => terminal.Render.Value is TerminalRenderState.Exited, "the exit");
        var exited = Assert.IsType<TerminalRenderState.Exited>(terminal.Render.Value);
        Assert.Same(session, exited.Session);
        Assert.False(terminal.IsAcceptingInput);
        Assert.True(terminal.CanStart);
    }

    [Fact]
    public void StartingAgainAfterAnExit_EndsTheOldShellAndSpawnsANewOne()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        var first = Start(terminal, launch, dispatcher);
        launch.Pty.ShellExits();
        Pump.WaitFor(dispatcher, () => terminal.Render.Value is TerminalRenderState.Exited, "the exit");

        terminal.Start();

        Running(terminal, dispatcher);
        Assert.Equal(2, launch.Starts);
        Assert.NotSame(first, Session(terminal));
    }

    [Fact]
    public void AFailedStart_SaysWhyAndCanBeStartedAgain()
    {
        var launch = new CountingLaunch { FailWith = "no shell here" };
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        terminal.ReportViewport(Viewport);

        terminal.Start();

        Pump.WaitFor(dispatcher, () => terminal.Render.Value is TerminalRenderState.Failed, "the failure");
        var failed = Assert.IsType<TerminalRenderState.Failed>(terminal.Render.Value);
        Assert.Equal("no shell here", failed.Message);
        Assert.True(terminal.CanStart);
    }

    [Fact]
    public void DisposingWithASpawnInFlight_EndsTheShellThatLands()
    {
        // The spawn posts its result to a loop that is about to stop. Nothing runs the post, so the
        // shell it carried would be a process owned by nobody for the rest of the machine's day.
        var launch = new CountingLaunch();
        var dispatcher = new BlockedDispatcher();
        var terminal = new TerminalInstance(launch, dispatcher);
        terminal.ReportViewport(Viewport);
        terminal.Start();
        Assert.True(launch.Started.Wait(TimeSpan.FromSeconds(5)), "The shell never started.");

        terminal.Dispose();

        Assert.True(
            SpinWait.SpinUntil(() => launch.Pty.IsDisposed, TimeSpan.FromSeconds(5)),
            "The spawned shell outlived the terminal that was disposed before adopting it.");
    }

    [Fact]
    public void ScrollingAnExitedScreen_StillWorks()
    {
        var launch = new CountingLaunch();
        var dispatcher = new QueuedDispatcher();
        using var terminal = new TerminalInstance(launch, dispatcher);
        Start(terminal, launch, dispatcher);
        launch.Pty.Emit(string.Join("\r\n", Enumerable.Range(0, 200).Select(i => $"line {i}")));
        Pump.WaitFor(dispatcher, () => Session(terminal)!.Grid.ScrollbackRows > 0, "the output");
        launch.Pty.ShellExits();
        Pump.WaitFor(dispatcher, () => terminal.Render.Value is TerminalRenderState.Exited, "the exit");

        Assert.True(terminal.Scroll(5), "The history a finished shell printed is still readable.");
    }

    static TerminalSession Start(
        TerminalInstance terminal, CountingLaunch launch, QueuedDispatcher dispatcher)
    {
        terminal.ReportViewport(Viewport);
        terminal.Start();
        Running(terminal, dispatcher);
        return Session(terminal)!;
    }

    static void Running(TerminalInstance terminal, QueuedDispatcher dispatcher) =>
        Pump.WaitFor(
            dispatcher,
            () => terminal.Render.Value is TerminalRenderState.Running,
            "the shell to be adopted");

    static TerminalSession? Session(TerminalInstance terminal) => terminal.Render.Value switch
    {
        TerminalRenderState.Running running => running.Session,
        TerminalRenderState.Exited exited => exited.Session,
        TerminalRenderState.Faulted faulted => faulted.Session,
        _ => null,
    };

    /// <summary>A launch that counts what it was asked for, over a terminal that stays open.</summary>
    sealed class CountingLaunch : ITerminalLaunch
    {
        public LifecyclePty Pty { get; private set; } = new();

        public string? FailWith { get; init; }

        public int Starts { get; private set; }

        public TerminalSize StartedAt { get; private set; }

        /// <summary>Signals that a spawn has happened, for a test whose dispatcher never runs.</summary>
        public ManualResetEventSlim Started { get; } = new();

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher)
        {
            Starts++;
            StartedAt = size;

            if (FailWith is { } message) throw new InvalidOperationException(message);

            // A second start is a second shell: the pseudo-terminal the first one ended goes with it.
            if (Pty.IsDisposed || Pty.HasExited) Pty = new LifecyclePty();

            var pty = Pty;
            var session = TerminalSession.Start(
                () => pty, new XtermSharpEngineFactory(), size, dispatcher);
            Started.Set();
            return session;
        }
    }

    /// <summary>A dispatcher that takes posts and never runs them, which is what a UI loop that has
    /// stopped looks like from the spawning thread.</summary>
    sealed class BlockedDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
        }
    }
}

/// <summary>
/// A pseudo-terminal that says whether it was disposed, so a test can assert a shell was actually
/// ended rather than merely dropped.
/// </summary>
internal sealed class LifecyclePty : IPtySession
{
    readonly BlockingCollection<byte[]> _output = new();
    readonly TaskCompletionSource<PtyExit> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    byte[] _current = [];
    int _offset;
    volatile bool _disposed;
    volatile bool _shellExited;

    public Task<PtyExit> Exited => _exited.Task;

    public bool IsDisposed => _disposed;

    public bool HasExited => _shellExited;

    public void Emit(string vt) => _output.Add(Encoding.UTF8.GetBytes(vt));

    public void ShellExits()
    {
        _shellExited = true;
        _output.CompleteAdding();
        _exited.TrySetResult(new PtyExit.Completed(0));
    }

    public int ReadOutput(Span<byte> buffer)
    {
        while (_offset >= _current.Length)
        {
            byte[] next;
            try
            {
                if (!_output.TryTake(out next!, Timeout.Infinite)) return 0;
            }
            catch (Exception)
            {
                // Completed or disposed while blocked: the stream has ended, which is not a failure.
                return 0;
            }

            _current = next;
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public void WriteInput(ReadOnlySpan<byte> bytes)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LifecyclePty));
    }

    public void Resize(PtySize size)
    {
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.CompleteAdding();
        _exited.TrySetResult(new PtyExit.Completed(0));
    }
}
