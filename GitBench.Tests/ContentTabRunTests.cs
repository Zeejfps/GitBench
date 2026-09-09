using GitBench.App;
using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The content panel's run of opened tabs: that shells and files interleave in the order they were
/// opened whichever kind came first, and that dragging one into a new place is the new order.
/// </summary>
public sealed class ContentTabRunTests : IDisposable
{
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<TerminalTabs> _shells = [];

    public void Dispose()
    {
        foreach (var shells in _shells) shells.Dispose();
    }

    [Fact]
    public void ANewShell_GoesAfterTheFilesAlreadyOpen()
    {
        // The whole reason the run exists: a shell asked for with files already open belongs after
        // them, not in front of them.
        var files = Files();
        var shells = Shells();
        files.Open("a.cs", pinned: true);
        using var run = Run(shells, files);

        shells.StartNew();

        Assert.Equal(["a.cs", "shell"], Names(run));
    }

    [Fact]
    public void ANewFile_GoesAfterTheShellsAlreadyRunning()
    {
        var files = Files();
        var shells = Shells();
        shells.StartNew();
        using var run = Run(shells, files);

        files.Open("a.cs", pinned: true);

        Assert.Equal(["shell", "a.cs"], Names(run));
    }

    [Fact]
    public void TheRunSeedsItselfInTheOrderThingsWereOpened()
    {
        // Not shells-then-files: the two lists know nothing of each other, and the seed is the only
        // place their orders have to be woven back together.
        var files = Files();
        var shells = Shells();
        files.Open("a.cs", pinned: true);
        shells.StartNew();
        files.Open("b.cs", pinned: true);

        using var run = Run(shells, files);

        Assert.Equal(["a.cs", "shell", "b.cs"], Names(run));
    }

    [Fact]
    public void DraggingATabRightwards_LandsItAfterTheOneItPassed()
    {
        var files = Files();
        files.Open("a.cs", pinned: true);
        files.Open("b.cs", pinned: true);
        files.Open("c.cs", pinned: true);
        using var run = Run(null, files);

        run.Move(0, 2);

        Assert.Equal(["b.cs", "c.cs", "a.cs"], Names(run));
    }

    [Fact]
    public void DraggingATabLeftwards_LandsItBeforeTheOneItPassed()
    {
        var files = Files();
        files.Open("a.cs", pinned: true);
        files.Open("b.cs", pinned: true);
        files.Open("c.cs", pinned: true);
        using var run = Run(null, files);

        run.Move(2, 0);

        Assert.Equal(["c.cs", "a.cs", "b.cs"], Names(run));
    }

    [Fact]
    public void AnArrangementDraggedIntoPlace_SurvivesTheRunBeingRebuilt()
    {
        // Switching repositories away and back builds another run, which seeds itself from the order
        // the tabs carry. An arrangement held only in the old list would not come back.
        var files = Files();
        files.Open("a.cs", pinned: true);
        files.Open("b.cs", pinned: true);
        var run = Run(null, files);
        run.Move(1, 0);
        run.Dispose();

        using var rebuilt = Run(null, files);

        Assert.Equal(["b.cs", "a.cs"], Names(rebuilt));
    }

    [Fact]
    public void AShellOpenedAfterADrag_IsStillLast()
    {
        // The renumbering a drag does has to leave room after it, or the next tab would land in the
        // middle of the arrangement the reader just made.
        var files = Files();
        var shells = Shells();
        files.Open("a.cs", pinned: true);
        files.Open("b.cs", pinned: true);
        using var run = Run(shells, files);
        run.Move(1, 0);

        shells.StartNew();

        Assert.Equal(["b.cs", "a.cs", "shell"], Names(run));
    }

    [Fact]
    public void MovingToWhereItAlreadyIs_ChangesNothing()
    {
        var files = Files();
        files.Open("a.cs", pinned: true);
        files.Open("b.cs", pinned: true);
        using var run = Run(null, files);

        run.Move(1, 1);
        run.Move(-1, 0);
        run.Move(0, 5);

        Assert.Equal(["a.cs", "b.cs"], Names(run));
    }

    [Fact]
    public void ClosingATab_TakesItOutOfTheRun()
    {
        var files = Files();
        files.Open("a.cs", pinned: true);
        var b = files.Open("b.cs", pinned: true);
        using var run = Run(null, files);

        files.Close(b);

        Assert.Equal(["a.cs"], Names(run));
    }

    [Fact]
    public void ATransientTabTakenOverByAnother_KeepsItsPlaceInTheRun()
    {
        // Arrowing down the tree takes the borrowed tab over and over; a file that jumped to the end
        // of the strip on every keystroke would be the shuffling that slot exists to avoid.
        var files = Files();
        files.Open("a.cs", pinned: true);
        files.Open("borrowed.cs", pinned: false);
        var shells = Shells();
        shells.StartNew();
        using var run = Run(shells, files);
        Assert.Equal(["a.cs", "borrowed.cs", "shell"], Names(run));

        files.Open("next.cs", pinned: false);

        Assert.Equal(["a.cs", "next.cs", "shell"], Names(run));
    }

    private static string[] Names(ContentTabRun run) => run.Tabs.Select(Name).ToArray();

    private static string Name(ContentTab tab) => tab switch
    {
        ContentTab.File file => file.Tab.Name,
        ContentTab.Shell shell => TerminalTabLabels.NameOf(shell.Instance),
        _ => "?",
    };

    private static ContentTabRun Run(TerminalTabs? shells, FileBrowserTabs? files) =>
        new(shells?.Terminals, files?.Items);

    private static FileBrowserTabs Files() => new(_ => false);

    private TerminalTabs Shells()
    {
        var shells = new TerminalTabs(() => new TerminalInstance(new NeverSpawns(), _dispatcher));
        _shells.Add(shells);
        return shells;
    }

    /// <summary>A launch that never spawns: this suite is about the run, not the shells.</summary>
    private sealed class NeverSpawns : ITerminalLaunch
    {
        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            throw new NotSupportedException("This suite never lets a shell spawn.");
    }
}
