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
    public void ARepository_StartsWithOneTerminalAndItIsTheActiveOne()
    {
        // Not zero: a terminal pane with no terminal in it is a state the pane would have to draw
        // something for and the reader would have no way out of.
        using var tabs = Tabs();

        Assert.Single(tabs.Terminals);
        Assert.Same(tabs.Terminals[0], tabs.Active.Value);
    }

    [Fact]
    public void OpeningATab_AddsItAndPutsItOnScreen()
    {
        using var tabs = Tabs();
        var first = tabs.Active.Value;

        var opened = tabs.Open();

        Assert.Equal(2, tabs.Terminals.Count);
        Assert.Same(opened, tabs.Active.Value);
        Assert.NotSame(first, opened);
    }

    [Fact]
    public void OpeningATab_StartsNothing()
    {
        // Making a terminal is not starting a shell — only a click is.
        using var tabs = Tabs();

        var opened = tabs.Open();

        Assert.IsType<TerminalRenderState.Idle>(opened.Render.Value);
        Assert.DoesNotContain(_launches, l => l.Started);
    }

    [Fact]
    public void ARepositoryWhoseTerminalHasNeverRun_HasNothingStarted()
    {
        // What the pane reads to decide there is no strip to draw yet: the offer to start a shell
        // stands on its own, with no tab naming one that does not exist.
        using var tabs = Tabs();

        Assert.False(tabs.AnyStarted);
    }

    [Fact]
    public void OnceATerminalHasRun_SomethingIsStarted()
    {
        using var tabs = Tabs();

        StartShell(tabs.Active.Value);

        Assert.True(tabs.AnyStarted);
    }

    [Fact]
    public void AShellThatHasExited_StillCountsAsStarted()
    {
        // The screen it left is still readable, and the tab is how it stays reachable.
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;
        StartShell(terminal);

        _launches[0].Pty!.ShellExits();
        Pump.WaitFor(_dispatcher, () => terminal.Render.Value is TerminalRenderState.Exited, "the exit");

        Assert.True(tabs.AnyStarted);
    }

    [Fact]
    public void ClosingTheLastTab_LeavesAFreshIdleTerminalInItsPlace()
    {
        // Not an empty strip: the repository goes back to the state it was activated in, which is
        // the offer to start a shell with no strip over it.
        using var tabs = Tabs();
        var only = tabs.Active.Value;
        StartShell(only);

        tabs.Close(only);

        var replacement = Assert.Single(tabs.Terminals);
        Assert.NotSame(only, replacement);
        Assert.Same(replacement, tabs.Active.Value);
        Assert.IsType<TerminalRenderState.Idle>(replacement.Render.Value);
    }

    [Fact]
    public void ClosingTheLastTab_EndsItsShellAndTakesTheStripWithIt()
    {
        using var tabs = Tabs();
        var only = tabs.Active.Value;
        StartShell(only);

        tabs.Close(only);

        Assert.True(_launches[0].Pty!.IsDisposed, "Closing the last tab left its shell running.");
        Assert.False(tabs.HasLiveShell);
        Assert.False(tabs.AnyStarted);
    }

    [Fact]
    public void ClosingTheActiveTab_LeavesTheNeighbourOnScreen()
    {
        using var tabs = Tabs();
        var first = tabs.Active.Value;
        var second = tabs.Open();

        tabs.Close(second);

        Assert.Equal(new[] { first }, tabs.Terminals.ToArray());
        Assert.Same(first, tabs.Active.Value);
    }

    [Fact]
    public void ClosingATabThatIsNotOnScreen_LeavesTheActiveOneAlone()
    {
        using var tabs = Tabs();
        var first = tabs.Terminals[0];
        var second = tabs.Open();

        tabs.Close(first);

        Assert.Same(second, tabs.Active.Value);
        Assert.Equal(new[] { second }, tabs.Terminals.ToArray());
    }

    [Fact]
    public void ClosingATab_EndsItsShell()
    {
        using var tabs = Tabs();
        tabs.Open();
        var terminal = tabs.Active.Value;
        StartShell(terminal);

        tabs.Close(terminal);

        Assert.True(_launches[1].Pty!.IsDisposed, "Closing the tab left its shell running.");
    }

    [Fact]
    public void ClosingATerminalThatHasAlreadyGone_IsANoOp()
    {
        // The confirmation is modal to the window and this list is not frozen while it is up: a
        // repository can close and a shell can exit between the middle click and the answer.
        using var tabs = Tabs();
        var second = tabs.Open();
        var third = tabs.Open();
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
        StartShell(tabs.Active.Value);
        tabs.Open();

        Assert.False(tabs.Active.Value.HasLiveShell);
        Assert.True(tabs.HasLiveShell);
    }

    [Fact]
    public void WithNothingStarted_NothingIsHoldingAShell()
    {
        using var tabs = Tabs();
        tabs.Open();

        Assert.False(tabs.HasLiveShell);
    }

    [Fact]
    public void ActivatingATerminalThatIsNotHere_IsANoOp()
    {
        using var tabs = Tabs();
        using var other = new TerminalInstance(NewLaunch(), _dispatcher);
        var active = tabs.Active.Value;

        tabs.Activate(other);

        Assert.Same(active, tabs.Active.Value);
    }

    [Fact]
    public void DisposingTheTabs_EndsEveryShell()
    {
        var tabs = Tabs();
        StartShell(tabs.Active.Value);
        StartShell(tabs.Open());

        tabs.Dispose();

        Assert.All(_launches, launch => Assert.True(launch.Pty!.IsDisposed));
    }

    [Fact]
    public void ATerminalWithNoTitle_IsCalledAfterItsShell()
    {
        using var tabs = Tabs();

        Assert.Equal("shell", TerminalTabLabels.NameOf(tabs.Active.Value));
    }

    [Fact]
    public void ATerminalWhoseProgramSetATitle_IsCalledThat()
    {
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;
        StartShell(terminal);

        _launches[0].Pty!.Emit("\u001b]2;vim README.md\u0007");
        Pump.WaitFor(_dispatcher, () => terminal.Title.Value == "vim README.md", "the title");

        Assert.Equal("vim README.md", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void ATerminalTheUserNamed_IsCalledThat()
    {
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;

        terminal.Rename("build");

        Assert.Equal("build", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void ANameTheUserGave_OutranksWhateverTheProgramSets()
    {
        // A tab is renamed precisely so it stops following the running command.
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;
        StartShell(terminal);
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
        var terminal = tabs.Active.Value;
        StartShell(terminal);
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
        var terminal = tabs.Active.Value;
        terminal.Rename("build");

        terminal.Rename("   ");

        Assert.Null(terminal.GivenName.Value);
        Assert.Equal("shell", TerminalTabLabels.NameOf(terminal));
    }

    [Fact]
    public void AGivenName_IsTrimmed()
    {
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;

        terminal.Rename("  build  ");

        Assert.Equal("build", terminal.GivenName.Value);
    }

    [Fact]
    public void RenamingATerminalThatHasGone_IsANoOp()
    {
        // The dialog is answered later, and the tab it was opened from can be closed in between.
        using var tabs = Tabs();
        tabs.Open();
        var terminal = tabs.Active.Value;
        tabs.Close(terminal);

        terminal.Rename("build");

        Assert.Null(terminal.GivenName.Value);
    }

    [Fact]
    public void TabsTheUserNamedTheSame_AreNumbered()
    {
        using var tabs = Tabs();
        var first = tabs.Active.Value;
        var second = tabs.Open();
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
        tabs.Open();
        tabs.Open();

        var labels = tabs.Terminals.Select(t => TerminalTabLabels.For(tabs.Terminals, t)).ToArray();

        Assert.Equal(new int?[] { 1, 2, 3 }, labels.Select(l => l.Index));
        Assert.All(labels, label => Assert.Equal("shell", label.Text));
    }

    [Fact]
    public void ATabWhoseNameNothingElseShares_IsNotNumbered()
    {
        using var tabs = Tabs();
        var terminal = tabs.Active.Value;
        StartShell(terminal);
        tabs.Open();

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

    void StartShell(TerminalInstance terminal)
    {
        terminal.ReportViewport(Viewport);
        terminal.Start();
        Pump.WaitFor(
            _dispatcher,
            () => terminal.Render.Value is TerminalRenderState.Running,
            "the shell to be adopted");
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
