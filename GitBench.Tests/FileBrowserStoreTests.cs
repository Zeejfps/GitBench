using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

public class FileBrowserStoreTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-filebrowser-store-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly FakeFileSystem _files = new();
    private readonly string _statePath;
    private RepoRegistry _registry;
    private readonly Guid _first;
    private readonly Guid _second;

    public FileBrowserStoreTests()
    {
        _statePath = Path.Combine(_dir.Path, "state.json");
        _registry = new RepoRegistry(RepoStateStore.Load(_statePath), _statePath);
        _first = OpenRepo("first");
        _second = OpenRepo("second");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _dir.Dispose();
    }

    private Guid OpenRepo(string name)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        _registry.Open(path);

        var root = Path.GetFullPath(path);
        _files.With(root, Dir("src"), File("README.md"));
        _files.With(Path.Combine(root, "src"), File("main.cs"));
        return _registry.Active.Value!.Id;
    }

    private string RepoPath(Guid id) => Path.GetFullPath(_registry.Repos.Single(r => r.Id == id).Path);

    private static FileSystemEntry Dir(string name) => new(name, true, false, false);
    private static FileSystemEntry File(string name) => new(name, false, false, false);

    private sealed class FakeFileSystem : IFileSystemReader
    {
        private readonly Dictionary<string, List<FileSystemEntry>> _directories = new(StringComparer.Ordinal);
        private readonly List<string> _listed = [];

        /// <summary>How many directory reads have landed under one working tree. Per root, because
        /// the store gives whichever repository is active when it starts a browser of its own, so a
        /// global count cannot tell the two apart.</summary>
        public int ListsUnder(string root) =>
            _listed.Count(path => path.StartsWith(root, StringComparison.Ordinal));

        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
        {
            _listed.Add(absoluteDirectory);
            return _directories.TryGetValue(absoluteDirectory, out var entries)
                ? new DirectoryListing.Listed(entries)
                : new DirectoryListing.Unavailable("No such directory.");
        }

        public string? ResolveLinkTarget(string absolutePath) => null;

        public void With(string directory, params FileSystemEntry[] entries) =>
            _directories[directory] = [.. entries];
    }

    private sealed class NoIgnores : IGitRepositoryReader
    {
        public bool IsPathTracked(Repo repo, string relativePath) => false;
        public bool IsPathIgnored(Repo repo, string relativePath) => false;
        public IReadOnlySet<string> IsPathIgnored(Repo repo, IReadOnlyList<string> relativePaths) =>
            new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<string> ListTrackedFiles(Repo repo) => [];
    }

    private FileBrowserStore Store()
    {
        var store = new FileBrowserStore(_registry, new NoIgnores(), _files, new UnparsedFiles(), _bus, _dispatcher);
        store.Start();
        return store;
    }

    private void Settle(FileBrowserViewModel browser)
    {
        for (var i = 0; i < 10; i++)
        {
            browser.Pending.GetAwaiter().GetResult();
            _dispatcher.Drain();
        }
    }

    private string[] Names(FileBrowserViewModel browser) =>
        browser.Rows.Value.Select(r => r.Name).ToArray();

    [Fact]
    public void ActivatingARepoPublishesItsBrowser()
    {
        using var store = Store();

        _registry.SetActive(_first);
        var browser = Assert.IsType<FileBrowserViewModel>(store.Active.Value);
        Settle(browser);

        Assert.Equal(["src", "README.md"], Names(browser));
    }

    [Fact]
    public void EachRepoKeepsItsOwnBrowserAcrossASwitch()
    {
        using var store = Store();

        _registry.SetActive(_first);
        var first = store.Active.Value!;
        _registry.SetActive(_second);
        var second = store.Active.Value!;
        _registry.SetActive(_first);

        Assert.NotSame(first, second);
        Assert.Same(first, store.Active.Value);
    }

    [Fact]
    public void AnIndexOnlyChangeDoesNotReListAnything()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var browser = store.Active.Value!;
        Settle(browser);
        var before = _files.ListsUnder(RepoPath(_first));

        _bus.Broadcast(new WorkingTreeChangedMessage(_first, IndexOnly: true));
        Settle(browser);

        Assert.Equal(before, _files.ListsUnder(RepoPath(_first)));
    }

    [Fact]
    public void AWorkingTreeChangeReListsWithoutLosingTheExpandedSetOrTheCursor()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var browser = store.Active.Value!;
        Settle(browser);

        var root = RepoPath(_first);
        browser.Toggle((FileBrowserRow.Directory)browser.Rows.Value[0]);
        Settle(browser);
        browser.SetCursor(Path.Combine(root, "src", "main.cs"));
        Assert.Equal(["src", "main.cs", "README.md"], Names(browser));

        _files.With(Path.Combine(root, "src"), File("main.cs"), File("generated.cs"));
        _bus.Broadcast(new WorkingTreeChangedMessage(_first));
        Settle(browser);

        Assert.Equal(["src", "generated.cs", "main.cs", "README.md"], Names(browser));
        Assert.Equal(Path.Combine(root, "src", "main.cs"), browser.Cursor.Value);
    }

    [Fact]
    public void AChangeInAnotherRepoLeavesThisBrowserAlone()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var browser = store.Active.Value!;
        Settle(browser);
        var before = _files.ListsUnder(RepoPath(_first));

        _bus.Broadcast(new WorkingTreeChangedMessage(_second));
        Settle(browser);

        Assert.Equal(before, _files.ListsUnder(RepoPath(_first)));
    }

    [Fact]
    public void ARepoWithNoBrowserYetIsNotGivenOneByATimerBroadcast()
    {
        using var store = Store();

        _bus.Broadcast(new WorkingTreeChangedMessage(_first));

        Assert.Equal(0, _files.ListsUnder(RepoPath(_first)));
    }

    [Fact]
    public void ClosingARepoTakesItsBrowserWithIt()
    {
        using var store = Store();
        _registry.SetActive(_first);
        var browser = store.Active.Value!;
        Settle(browser);

        _registry.RemoveRepo(_first);

        Assert.NotSame(browser, store.Active.Value);
    }

    [Fact]
    public void APersistedPathThatEscapesTheRepoIsNotRestored()
    {
        _registry.SetFileBrowserUi(_first, new FileBrowserUiState
        {
            Expanded = ["../..", Path.GetFullPath(Path.GetTempPath())],
            Cursor = "../../../etc/passwd",
        });

        using var store = Store();
        _registry.SetActive(_first);
        var browser = store.Active.Value!;
        Settle(browser);

        Assert.Equal(["src", "README.md"], Names(browser));
        Assert.Null(browser.Cursor.Value);
    }

    [Fact]
    public void TheOpenDirectoriesAndTheCursorSurviveARestart()
    {
        var root = RepoPath(_first);
        using (var store = Store())
        {
            _registry.SetActive(_first);
            var browser = store.Active.Value!;
            Settle(browser);
            browser.Toggle((FileBrowserRow.Directory)browser.Rows.Value[0]);
            Settle(browser);
            browser.SetCursor(Path.Combine(root, "src", "main.cs"));
            browser.SetShowHidden(false);
            Settle(browser);
        }

        _registry.Dispose();
        _registry = new RepoRegistry(RepoStateStore.Load(_statePath), _statePath);

        using var reopened = Store();
        _registry.SetActive(_first);
        var restored = reopened.Active.Value!;
        Settle(restored);

        Assert.Equal(["src", "main.cs", "README.md"], Names(restored));
        Assert.Equal(Path.Combine(root, "src", "main.cs"), restored.Cursor.Value);
        Assert.False(restored.ShowHidden.Value);
    }
}
