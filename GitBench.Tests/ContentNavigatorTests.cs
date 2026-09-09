using GitBench.App;
using GitBench.Features.CodeIntel;
using GitBench.Features.FileBrowser;
using GitBench.Features.Terminal;
using GitBench.Git;
using GitBench.Pty;
using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The content panel's trail: that back and forward walk every tab in the strip rather than only
/// the files, and that a place whose tab has gone is stepped past rather than stepped onto.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class ContentNavigatorTests(CodeIntelFixture fixture) : IDisposable
{
    private static readonly TerminalSize Viewport = new(100, 37);

    private readonly TempDir _dir = new("gitbench-content-nav-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly List<FileBrowserUiState> _persisted = [];
    private readonly State<MainViewMode> _mode = new(MainViewMode.LocalChanges);
    private readonly List<TabsLaunch> _launches = [];

    private FileBrowserViewModel? _browser;
    private OneBrowser? _browsers;
    private TerminalTabs? _shells;
    private OneRepoTerminals? _terminals;
    private ContentNavigator? _navigator;

    public void Dispose()
    {
        _navigator?.Dispose();
        _browsers?.Dispose();
        _shells?.Dispose();
        _browser?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void WithNothingBehindIt_TheBackArrowIsDead()
    {
        Start();

        Assert.False(Nav.CanGoBack.Value);
        Assert.False(Nav.CanGoForward.Value);
    }

    [Fact]
    public void SwitchingBetweenTheTwoViews_IsAStepEitherWay()
    {
        Start();

        Nav.Show(new ContentPlace.View(MainViewMode.History));

        Assert.Equal(MainViewMode.History, _mode.Value);
        Assert.True(Nav.CanGoBack.Value);

        Nav.GoBack();

        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
        Assert.False(Nav.CanGoBack.Value);
        Assert.True(Nav.CanGoForward.Value);

        Nav.GoForward();

        Assert.Equal(MainViewMode.History, _mode.Value);
    }

    [Fact]
    public void AskingForTheViewAlreadyOnScreen_IsNotAStep()
    {
        Start();

        Nav.Show(new ContentPlace.View(MainViewMode.LocalChanges));

        Assert.False(Nav.CanGoBack.Value);
    }

    [Fact]
    public void OpeningAFileFromAnotherTab_ComesBackToThatTab()
    {
        // The whole point of moving the trail up here: the tree is in the sidebar and the tab it
        // fills is in the panel, so neither of them could have recorded this step.
        Write("Auth.cs", "class AuthService", "{", "}");
        Start();
        Nav.Show(new ContentPlace.View(MainViewMode.History));

        Open("Auth.cs");

        Assert.Equal(MainViewMode.Files, _mode.Value);

        Nav.GoBack();
        Settle();

        Assert.Equal(MainViewMode.History, _mode.Value);
    }

    [Fact]
    public void AWholeTrailAcrossEveryKindOfTab_IsWalkedBackInOrder()
    {
        Write("Auth.cs", "class AuthService", "{", "}");
        Start();

        Nav.Show(new ContentPlace.View(MainViewMode.History));
        var shell = _shells!.StartNew();
        Nav.Show(new ContentPlace.Shell(shell));
        Open("Auth.cs");
        Assert.Equal(MainViewMode.Files, _mode.Value);

        Nav.GoBack();
        Settle();
        Assert.Equal(MainViewMode.Terminal, _mode.Value);
        Assert.Same(shell, _shells.Active.Value);

        Nav.GoBack();
        Settle();
        Assert.Equal(MainViewMode.History, _mode.Value);

        Nav.GoBack();
        Settle();
        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
        Assert.False(Nav.CanGoBack.Value);
    }

    [Fact]
    public void AShellThatHasBeenClosed_IsSteppedPastRatherThanSteppedOnto()
    {
        // Otherwise the press does nothing at all, which reads as the arrow being broken.
        Start();
        Nav.Show(new ContentPlace.View(MainViewMode.History));
        var shell = _shells!.StartNew();
        Nav.Show(new ContentPlace.Shell(shell));
        Nav.Show(new ContentPlace.View(MainViewMode.LocalChanges));

        _shells.Close(shell);

        Nav.GoBack();

        Assert.Equal(MainViewMode.History, _mode.Value);
    }

    [Fact]
    public void GoingSomewhereNew_DropsWhatWasAhead()
    {
        Start();
        Nav.Show(new ContentPlace.View(MainViewMode.History));
        Nav.GoBack();
        Assert.True(Nav.CanGoForward.Value);

        Nav.Show(new ContentPlace.View(MainViewMode.History));

        Assert.False(Nav.CanGoForward.Value);
    }

    [Fact]
    public void ClosingTheLastFile_HandsThePanelBackWithoutRecordingAStep()
    {
        // Nobody navigated: the tab simply stopped existing, and a trail that recorded it would
        // send the reader back to a file they had just closed.
        Write("Auth.cs", "class AuthService", "{", "}");
        Start();
        Open("Auth.cs");
        var wasBack = Nav.CanGoBack.Value;

        _browser!.CloseAllTabs();
        Settle();

        Assert.Equal(MainViewMode.LocalChanges, _mode.Value);
        Assert.Equal(wasBack, Nav.CanGoBack.Value);
    }

    private ContentNavigator Nav => _navigator!;

    private void Start()
    {
        _browser = new FileBrowserViewModel(
            new Repo(Guid.NewGuid(), _dir.Path, "repo"),
            new FileSystemReader(),
            NoIgnoreOracle.Instance,
            EmptyFileCatalog.Instance,
            fixture.Extractor,
            _dispatcher,
            new FileBrowserUiState(),
            _persisted.Add,
            TestDocuments.ForOneRepo(),
            TestDocuments.Discard,
            TestDocuments.KeepEdits);
        _browser.Invalidate();
        Settle();

        _shells = new TerminalTabs(() => new TerminalInstance(NewLaunch(), _dispatcher));
        _browsers = new OneBrowser(_browser);
        _terminals = new OneRepoTerminals(_shells);
        _navigator = new ContentNavigator(_browsers, _terminals, _mode);
        _navigator.Start();
    }

    private void Open(string relative)
    {
        _browser!.SetCursor(At(relative));
        Settle(() => _browser.Preview.Value is FilePreview.Text);
    }

    private string At(string relative) =>
        Path.Combine(_dir.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, params string[] lines)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private void Settle(Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            _browser!.Pending.Wait(TimeSpan.FromSeconds(5));
            _dispatcher.Drain();
            if (until is null || until()) return;
            Thread.Sleep(5);
        }

        if (until is not null) Assert.Fail("The browser never reached the expected state.");
    }

    private TabsLaunch NewLaunch()
    {
        var launch = new TabsLaunch();
        _launches.Add(launch);
        return launch;
    }

    /// <summary>One repository's terminals behind the store the panel reads.</summary>
    private sealed class OneRepoTerminals(TerminalTabs tabs) : ITerminalSessionStore
    {
        public IReadable<TerminalTabs?> Tabs { get; } = new State<TerminalTabs?>(tabs);

        public bool HasLiveShell(Guid repoId) => false;

        public IReadOnlyList<Guid> ReposWithLiveShells() => [];
    }

    /// <summary>A launch that never spawns: these tests are about the tabs, not the shells.</summary>
    private sealed class TabsLaunch : ITerminalLaunch
    {
        public string Name => "shell";

        public TerminalSize SizeFor(TerminalSize viewport) => viewport;

        public TerminalSession Start(TerminalSize size, IUiDispatcher dispatcher) =>
            throw new NotSupportedException("These tests never let a shell spawn.");
    }
}
