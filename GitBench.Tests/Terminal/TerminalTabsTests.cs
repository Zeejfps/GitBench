using GitBench.Features.Terminal;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Several terminals behind one repository: which one the pane draws, what opening and closing does
/// to that, and what a strip of them is called.
/// </summary>
public class TerminalTabsTests
{
    static readonly TerminalSize Viewport = new(100, 37);

    readonly QueuedDispatcher _dispatcher = new();
    readonly List<TabsLaunch> _launches = new();

    [Fact]
    public void ARepository_StartsWithNoTerminals()
    {
        // Not one waiting to be started: a terminal exists because someone pressed for one, so a
        // repository nobody has asked has nothing in its strip and nothing on screen.
        using var tabs = Tabs();

        Assert.Empty(tabs.Terminals);
        Assert.Null(tabs.Active.Value);
    }

    [Fact]
    public void StartingATerminal_AddsItPutsItOnScreenAndAsksForAShell()
    {
        using var tabs = Tabs();

        var opened = tabs.StartNew();

        Assert.Equal(new[] { opened }, tabs.Terminals.ToArray());
        Assert.Same(opened, tabs.Active.Value);
        Assert.IsType<TerminalRenderState.Starting>(opened.Render.Value);
    }

    [Fact]
    public void StartingASecondTerminal_LeavesTheFirstAndTakesTheScreen()
    {
        using var tabs = Tabs();
        var first = tabs.StartNew();

        var second = tabs.StartNew();

        Assert.Equal(new[] { first, second }, tabs.Terminals.ToArray());
        Assert.Same(second, tabs.Active.Value);
    }

    [Fact]
    public void TheSpawn_WaitsForTheGridToSayHowBigItIs()
    {
        // A shell has to be told its size, so asking for one is not yet running one — the grid
        // reporting a viewport is what finishes the gesture.
        using var tabs = Tabs();

        var terminal = tabs.StartNew();

        Assert.False(_launches[0].Started);
        terminal.ReportViewport(Viewport);
        Pump.WaitFor(
            _dispatcher, () => terminal.Render.Value is TerminalRenderState.Running, "the shell");
        Assert.True(_launches[0].Started);
    }

    [Fact]
    public void ClosingTheLastTab_LeavesTheRepositoryWithNone()
    {
        // An empty strip, not a fresh unstarted terminal: there is nothing left to be on screen,
        // and the panel goes back to whatever it was showing before.
        using var tabs = Tabs();
        var only = StartShell(tabs);

        tabs.Close(only);

        Assert.Empty(tabs.Terminals);
        Assert.Null(tabs.Active.Value);
    }

    [Fact]
    public void ClosingTheLastTab_EndsItsShell()
    {
        using var tabs = Tabs();
        var only = StartShell(tabs);

        tabs.Close(only);

        Assert.True(_launches[0].Pty!.IsDisposed, "Closing the last tab left its shell running.");
        Assert.False(tabs.HasLiveShell);
    }

    [Fact]
    public void ClosingTheActiveTab_LeavesTheNeighbourOnScreen()
    {
        using var tabs = Tabs();
        var first = tabs.StartNew();
        var second = tabs.StartNew();

        tabs.Close(second);

        Assert.Equal(new[] { first }, tabs.Terminals.ToArray());
        Assert.Same(first, tabs.Active.Value);
    }

    [Fact]
    public void ClosingATabThatIsNotOnScreen_LeavesTheActiveOneAlone()
    {
        using var tabs = Tabs();
        var first = tabs.StartNew();
        var second = tabs.StartNew();

        tabs.Close(first);

        Assert.Same(second, tabs.Active.Value);
        Assert.Equal(new[] { second }, tabs.Terminals.ToArray());
    }

    [Fact]
    public void ClosingATab_EndsItsShell()
    {
        using var tabs = Tabs();
        tabs.StartNew();
        var terminal = StartShell(tabs);

        tabs.Close(terminal);

        Assert.True(_launches[1].Pty!.IsDisposed, "Closing the tab left its shell running.");
    }

    [Fact]
    public void ClosingATerminalThatHasAlreadyGone_IsANoOp()
    {
        // The confirmation is modal to the window and this list is not frozen while it is up: a
        // repository can close and a shell can exit between the middle click and the answer.
        using var tabs = Tabs();
        tabs.StartNew();
        var second = tabs.StartNew();
        var third = tabs.StartNew();
        tabs.Close(second);

        tabs.Close(second);

        Assert.Equal(2, tabs.Terminals.Count);
        Assert.Same(third, tabs.Active.Value);
    }

    [Fact]
    public void AShellInATabThatIsNotOnScreen_StillCounts()
    {
        // The quit confirmation reads this, and a shell it cannot see is exactly the one it must not
        // forget to name.
        using var tabs = Tabs();
        var hidden = StartShell(tabs);
        tabs.StartNew();

        Assert.NotSame(hidden, tabs.Active.Value);
        Assert.True(hidden.HasLiveShell);
        Assert.True(tabs.HasLiveShell);
    }

    [Fact]
    public void ARepositoryWithNoTerminals_IsHoldingNoShell()
    {
        using var tabs = Tabs();

        Assert.False(tabs.HasLiveShell);
    }

    [Fact]
    public void ActivatingATerminalThatIsNotHere_IsANoOp()
    {
        using var tabs = Tabs();
        using var other = new TerminalInstance(NewLaunch(), _dispatcher);
        var active = tabs.StartNew();

        tabs.Activate(other);

        Assert.Same(active, tabs.Active.Value);
    }

    [Fact]
    public void DisposingTheTabs_EndsEveryShell()
    {
        var tabs = Tabs();
        StartShell(tabs);
        StartShell(tabs);

        tabs.Dispose();

        Assert.All(_launches, launch => Assert.True(launch.Pty!.IsDisposed));
    }

    [Fact]
    public void ATerminalWithNoTitle_IsCalledAfterItsShell()
    {
        using var tabs = Tabs();

        Assert.Equal("shell", TerminalTabLabels.NameOf(tabs.StartNew()));
    }

    [Fact]
    public void ATerminalWhoseProgramSetATitle_IsCalledThat()
    {
        using var tabs = Tabs();
        var terminal = StartShell(tabs);

        _launches[0].Pty!.Emit("\u001b]2;vim README.md\u0007");
        Pump.WaitFor(_dispatcher, () => terminal.Title.Value == "vim README.md", "the title");

        Assert.Equal("vim README.md", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void ATerminalTheUserNamed_IsCalledThat()
    {
        using var tabs = Tabs();
        var terminal = tabs.StartNew();

        terminal.Rename("build");

        Assert.Equal("build", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void ANameTheUserGave_OutranksWhateverTheProgramSets()
    {
        // A tab is renamed precisely so it stops following the running command.
        using var tabs = Tabs();
        var terminal = StartShell(tabs);
        terminal.Rename("build");

        _launches[0].Pty!.Emit("\u001b]2;vim README.md\u0007");
        Pump.WaitFor(_dispatcher, () => terminal.Title.Value == "vim README.md", "the title");

        Assert.Equal("build", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void DroppingTheGivenName_GoesBackToWhatIsRunningNow()
    {
        // Not to the title it had when the rename happened: the name is given back, not restored.
        using var tabs = Tabs();
        var terminal = StartShell(tabs);
        terminal.Rename("build");

        _launches[0].Pty!.Emit("\u001b]2;vim README.md\u0007");
        Pump.WaitFor(_dispatcher, () => terminal.Title.Value == "vim README.md", "the title");
        terminal.Rename(null);

        Assert.Null(terminal.GivenName.Value);
        Assert.Equal("vim README.md", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void ANameOfNothingButSpaces_IsNoName()
    {
        // An emptied field reads as asking for the name the tab had before it was touched, and
        // whitespace is not something a strip could show.
        using var tabs = Tabs();
        var terminal = tabs.StartNew();
        terminal.Rename("build");

        terminal.Rename("   ");

        Assert.Null(terminal.GivenName.Value);
        Assert.Equal("shell", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void AGivenName_IsTrimmed()
    {
        using var tabs = Tabs();
        var terminal = tabs.StartNew();

        terminal.Rename("  build  ");

        Assert.Equal("build", terminal.GivenName.Value);
    }

    [Fact]
    public void RenamingATerminalThatHasGone_IsANoOp()
    {
        // The dialog is answered later, and the tab it was opened from can be closed in between.
        using var tabs = Tabs();
        tabs.StartNew();
        var terminal = tabs.StartNew();
        tabs.Close(terminal);

        terminal.Rename("build");

        Assert.Null(terminal.GivenName.Value);
    }

    [Fact]
    public void TabsTheUserNamedTheSame_AreNumbered()
    {
        using var tabs = Tabs();
        var first = tabs.StartNew();
        var second = tabs.StartNew();
        first.Rename("build");
        second.Rename("build");

        var labels = new[] { first, second }.Select(t => TerminalTabLabels.For(tabs.Terminals, t)).ToArray();

        Assert.Equal(new int?[] { 1, 2 }, labels.Select(l => l.Index));
        Assert.All(labels, label => Assert.Equal("build", label.Text));
    }

    [Fact]
    public void TabsThatWouldReadTheSame_AreNumbered()
    {
        using var tabs = Tabs();
        tabs.StartNew();
        tabs.StartNew();
        tabs.StartNew();

        var labels = tabs.Terminals.Select(t => TerminalTabLabels.For(tabs.Terminals, t)).ToArray();

        Assert.Equal(new int?[] { 1, 2, 3 }, labels.Select(l => l.Index));
        Assert.All(labels, label => Assert.Equal("shell", label.Text));
    }

    [Fact]
    public void ATabWhoseNameNothingElseShares_IsNotNumbered()
    {
        using var tabs = Tabs();
        var terminal = StartShell(tabs);
        tabs.StartNew();

        _launches[0].Pty!.Emit("\u001b]2;claude\u0007");
        Pump.WaitFor(_dispatcher, () => terminal.Title.Value == "claude", "the title");

        Assert.Null(TerminalTabLabels.For(tabs.Terminals, terminal).Index);
        Assert.Null(TerminalTabLabels.For(tabs.Terminals, tabs.Terminals[1]).Index);
    }

    TerminalTabs Tabs() => new(() => new TerminalInstance(NewLaunch(), _dispatcher));

    TabsLaunch NewLaunch()
    {
        var launch = new TabsLaunch();
        _launches.Add(launch);
        return launch;
    }

    /// <summary>Asks for a terminal and lets its grid report a size, which is what the spawn waits
    /// on. Returns the terminal it made.</summary>
    TerminalInstance StartShell(TerminalTabs tabs)
    {
        var terminal = tabs.StartNew();
        terminal.ReportViewport(Viewport);
        Pump.WaitFor(
            _dispatcher,
            () => terminal.Render.Value is TerminalRenderState.Running,
            "the shell to be adopted");
        return terminal;
    }

    /// <summary>One terminal's launch, holding the pseudo-terminal it started so a test can drive it.</summary>
    sealed class TabsLaunch : ITerminalLaunch
    {
        public LifecyclePty? Pty { get; private set; }

        public bool Started => Pty is not null;

        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher)
        {
            var pty = new LifecyclePty();
            Pty = pty;
            return TerminalSession.Start(() => pty, new XtermSharpEngineFactory(), size, dispatcher);
        }
    }
}
