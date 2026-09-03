using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Localization;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// What the reader gets back for a Find Usages: a jump when there is one place to go, nothing at
// all when there is none, and a searchable menu otherwise. The harness supplies the real context
// menu host, so "a popup opened" is a fact about a mounted menu rather than about a call.
public class UsagesPopupTests
{
    private const string Root = "/repo";
    private const string File = "/repo/src/main.rs";

    // Nothing to show and nothing to dismiss. An empty popup would be a thing the reader has to
    // close to learn it said nothing.
    [Fact]
    public void ASymbolNothingUsesOpensNothing()
    {
        using var fx = new Fixture();

        fx.Show();

        Assert.Equal(0, fx.Harness.OpenMenuCount);
        Assert.Empty(fx.Navigator.Went);
    }

    // One usage is what a private helper has, and a menu of one row is a click the reader should
    // not have had to make.
    [Fact]
    public void ASingleUsageIsNavigatedToWithoutAPopup()
    {
        using var fx = new Fixture();
        fx.Servers.Sites = [In("src/lib.rs", 3)];
        fx.Files["/repo/src/lib.rs"] = ["one", "two", "    Compute(alpha);"];

        fx.Show();

        Assert.Equal(0, fx.Harness.OpenMenuCount);
        var went = Assert.Single(fx.Navigator.Went);
        Assert.Equal(Path.Combine(Root, "src", "lib.rs"), went.Path);
        Assert.Equal(3, went.Line);
    }

    [Fact]
    public void SeveralUsagesOpenAMenuOfWhereAndWhat()
    {
        using var fx = new Fixture();
        fx.Servers.Sites = [In("src/lib.rs", 3), In("src/main.rs", 1)];
        fx.Files["/repo/src/lib.rs"] = ["one", "two", "    Compute(alpha);"];
        fx.Files["/repo/src/main.rs"] = ["let alpha = 1;"];

        fx.Show();

        Assert.Equal(1, fx.Harness.OpenMenuCount);
        Assert.Empty(fx.Navigator.Went);
        var screen = fx.Harness.SnapshotWindows().ToText();
        Assert.Contains("src/lib.rs:3", screen);
        Assert.Contains("Compute(alpha);", screen);
        Assert.Contains("src/main.rs:1", screen);
    }

    [Fact]
    public void PickingARowNavigatesToThatUsage()
    {
        using var fx = new Fixture();
        fx.Servers.Sites = [In("src/lib.rs", 3), In("src/main.rs", 1)];
        fx.Files["/repo/src/lib.rs"] = ["one", "two", "    Compute(alpha);"];
        fx.Files["/repo/src/main.rs"] = ["let alpha = 1;"];
        fx.Show();

        fx.Harness.ClickMenuItem("src/lib.rs:3", exact: false);

        // The menu tears down before its action runs, and the teardown is applied on the next read.
        Assert.Equal(0, fx.Harness.OpenMenuCount);
        var went = Assert.Single(fx.Navigator.Went);
        Assert.Equal(Path.Combine(Root, "src", "lib.rs"), went.Path);
        Assert.Equal(3, went.Line);
    }

    // A reader shown a hundred of four hundred has to be told which number is which.
    [Fact]
    public void ACappedListSaysHowManyItIsShowing()
    {
        using var fx = new Fixture();
        fx.Servers.Sites = [.. Enumerable.Range(1, 150).Select(n => In("src/lib.rs", n))];
        fx.Files["/repo/src/lib.rs"] = [.. Enumerable.Range(1, 150).Select(n => $"line {n}")];

        fx.Show();

        Assert.Equal(1, fx.Harness.OpenMenuCount);
        Assert.Contains("Showing 100 of 150", fx.Harness.SnapshotWindows().ToText());
    }

    [Fact]
    public void AFileNoServerAnswersForIsNeverAsked()
    {
        using var fx = new Fixture();
        fx.Servers.Answers = false;

        // Nothing is asked and nothing is posted back, so there is nothing to wait for.
        fx.Ask();

        Assert.Empty(fx.Servers.Asked);
        Assert.Equal(0, fx.Harness.OpenMenuCount);
    }

    // The answer is late by definition; by the time it lands the reader may be reading something
    // else, and a menu about the file they left is a menu they never asked for.
    [Fact]
    public void AnAnswerAboutAFileTheReaderHasLeftShowsNothing()
    {
        using var fx = new Fixture();
        fx.Servers.Sites = [In("src/lib.rs", 3), In("src/main.rs", 1)];
        var held = new TaskCompletionSource();
        fx.Servers.Gate = held;

        fx.Ask();
        fx.Document = (Root, "/repo/src/other.rs");
        held.SetResult();
        fx.Settle();

        Assert.Equal(0, fx.Harness.OpenMenuCount);
        Assert.Empty(fx.Navigator.Went);
    }

    private static DefinitionTarget In(string relativePath, int oneBasedLine) =>
        new DefinitionTarget.InRepo(
            relativePath, new LspPosition(LspLine.FromOneBased(oneBasedLine), new LspCharacter(0)));

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Harness = GuiTestHarness.Create(
                _ => new ColumnView(),
                configure: ctx =>
                {
                    ctx.AddService<IThemeService<ThemeStyles>>(
                        new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                    ctx.AddService<ILocalizationService>(
                        new LocalizationService(new State<Locale>(Locale.En)));
                });

            Popup = new UsagesPopup(
                Harness.Context,
                Servers,
                Navigator,
                Dispatcher,
                () => Document,
                path => Files.TryGetValue(path.Replace('/', Path.DirectorySeparatorChar), out var lines)
                    ? lines
                    : null);
        }

        public GuiTestHarness Harness { get; }

        public UsagesPopup Popup { get; }

        public FakeReferences Servers { get; } = new();

        public FakeNavigator Navigator { get; } = new();

        public FileSet Files { get; } = new();

        public QueuedDispatcher Dispatcher { get; } = new();

        public (string Root, string Path)? Document { get; set; } = (Root, File);

        public void Show()
        {
            Ask();
            Settle();
        }

        public void Ask() =>
            Popup.ShowUsagesOf(new PointF(10, 10), new FileLine(8), new RawColumn(2));

        // The request runs on the thread pool and comes back through the dispatcher; running what
        // it posted on this thread is what the real UI thread does with it.
        public void Settle() =>
            Assert.True(Dispatcher.Drain(TimeSpan.FromSeconds(5)), "no answer reached the dispatcher");

        public void Dispose()
        {
            Popup.Dispose();
            Harness.Dispose();
        }
    }

    // Files keyed by native path, so the popup's own Path.Combine is what finds them.
    private sealed class FileSet
    {
        private readonly Dictionary<string, string[]> _files = new();

        public string[] this[string path]
        {
            set => _files[path.Replace('/', Path.DirectorySeparatorChar)] = value;
        }

        public bool TryGetValue(string path, out string[] lines) => _files.TryGetValue(path, out lines!);
    }

    private sealed class FakeReferences : IReferenceSource
    {
        public List<(string Path, FileLine Line, RawColumn Column)> Asked { get; } = [];

        public IReadOnlyList<DefinitionTarget> Sites { get; set; } = [];

        public bool Answers { get; set; } = true;

        // Created by the test before the question is asked, so releasing it can never outrun the
        // request reaching the await.
        public TaskCompletionSource? Gate { get; set; }

        public bool CanReference(string absolutePath) => Answers;

        public async Task<ReferenceReply> ReferencesAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct)
        {
            Asked.Add((absolutePath, line, column));
            if (Gate is { } gate) await gate.Task.ConfigureAwait(false);
            return new ReferenceReply.Answered(Sites);
        }
    }

    private sealed class FakeNavigator : IFileNavigator
    {
        public List<(string Path, int Line)> Went { get; } = [];

        public void NavigateTo(string absolutePath, int line) => Went.Add((absolutePath, line));

        public void GoBack() { }

        public void GoForward() { }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
        private readonly ManualResetEventSlim _arrived = new(initialState: false);

        public void Post(Action action)
        {
            _posted.Enqueue(action);
            _arrived.Set();
        }

        public bool Drain(TimeSpan timeout)
        {
            if (!_arrived.Wait(timeout)) return false;
            while (_posted.TryDequeue(out var action)) action();
            return true;
        }
    }
}
