using GitBench.Features.FileBrowser;
using Xunit;

namespace GitBench.Tests;

public class FileBrowserTreeTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "diffdino-filebrowser"));

    private static string At(params string[] segments) => Path.Combine([Root, .. segments]);

    private sealed class FakeFileSystem : IFileSystemReader
    {
        public readonly Dictionary<string, List<FileSystemEntry>> Directories = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> LinkTargets = new(StringComparer.Ordinal);
        public readonly List<string> Listed = [];

        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
        {
            Listed.Add(absoluteDirectory);
            return Directories.TryGetValue(absoluteDirectory, out var entries)
                ? new DirectoryListing.Listed(entries)
                : new DirectoryListing.Unavailable("No such directory.");
        }

        public string? ResolveLinkTarget(string absolutePath) =>
            LinkTargets.TryGetValue(absolutePath, out var target) ? target : null;

        public FakeFileSystem With(string directory, params FileSystemEntry[] entries)
        {
            Directories[directory] = [.. entries];
            return this;
        }
    }

    private sealed class FakeIgnoreOracle : IIgnoreOracle
    {
        public readonly HashSet<string> IgnoredPaths = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyList<string>> Batches = [];

        public IReadOnlySet<string> Ignored(IReadOnlyList<string> relativePaths)
        {
            Batches.Add(relativePaths);
            return relativePaths.Where(IgnoredPaths.Contains).ToHashSet(StringComparer.Ordinal);
        }
    }

    private static FileSystemEntry Dir(string name, bool isLink = false, bool isHidden = false) =>
        new(name, IsDirectory: true, IsLink: isLink, IsHidden: isHidden);

    private static FileSystemEntry File(string name, bool isHidden = false) =>
        new(name, IsDirectory: false, IsLink: false, IsHidden: isHidden);

    private static string[] Names(FileBrowserTree tree) => tree.Rows.Select(r => r.Name).ToArray();

    [Fact]
    public void RootListsImmediatelyAndNothingBelowIt()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("src"), File("README.md"))
            .With(At("src"), File("main.cs"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);

        Assert.Equal(["src", "README.md"], Names(tree));
        Assert.DoesNotContain(At("src"), fs.Listed);
    }

    [Fact]
    public void ExpandingListsChildrenAndCollapsingTakesThemBack()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("src"), File("README.md"))
            .With(At("src"), File("main.cs"), Dir("ui"))
            .With(At("src", "ui"), File("view.cs"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("src"));

        Assert.Equal(["src", "ui", "main.cs", "README.md"], Names(tree));
        Assert.True(tree.IsExpanded(At("src")));
        var uiRow = Assert.IsType<FileBrowserRow.Directory>(tree.Rows[1]);
        Assert.Equal(1, uiRow.Depth);
        Assert.False(uiRow.IsExpanded);

        tree.Collapse(At("src"));

        Assert.Equal(["src", "README.md"], Names(tree));
        Assert.False(tree.IsExpanded(At("src")));
    }

    [Fact]
    public void DirectoriesLeadAndNamesTieBreakOnCase()
    {
        var fs = new FakeFileSystem()
            .With(Root, File("readme"), Dir("Zed"), File("b.txt"), File("README"), Dir("alpha"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);

        Assert.Equal(["alpha", "Zed", "b.txt", "README", "readme"], Names(tree));
    }

    [Fact]
    public void ALinkBackUpItsOwnPathIsListedButNotWalked()
    {
        var fs = new FakeFileSystem().With(Root, Dir("up", isLink: true), File("a.txt"));
        fs.LinkTargets[At("up")] = Root;
        fs.With(At("up"), Dir("up", isLink: true), File("a.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("up"));

        var row = Assert.IsType<FileBrowserRow.Directory>(tree.Rows[0]);
        Assert.Equal("up", row.Name);
        Assert.True(row.IsLink);
        Assert.False(row.IsExpanded);
        Assert.False(tree.IsExpanded(At("up")));
        Assert.Equal(["up", "a.txt"], Names(tree));
    }

    [Fact]
    public void ALinkThatLeavesThePathIsWalkedNormally()
    {
        var elsewhere = Path.Combine(Root, "..", "elsewhere");
        var fs = new FakeFileSystem().With(Root, Dir("shared", isLink: true));
        fs.LinkTargets[At("shared")] = elsewhere;
        fs.With(At("shared"), File("thing.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("shared"));

        Assert.Equal(["shared", "thing.txt"], Names(tree));
        Assert.True(tree.IsExpanded(At("shared")));
    }

    [Fact]
    public void ExpansionStopsAtTheDepthCap()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("a"))
            .With(At("a"), Dir("b"))
            .With(At("a", "b"), Dir("c"))
            .With(At("a", "b", "c"), File("deep.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root, maxDepth: 2);
        tree.Expand(At("a"));
        tree.Expand(At("a", "b"));

        Assert.Equal(["a", "b"], Names(tree));
        Assert.True(tree.IsExpanded(At("a")));
        Assert.False(tree.IsExpanded(At("a", "b")));
        Assert.DoesNotContain(At("a", "b"), fs.Listed);
    }

    [Fact]
    public void ADirectoryDeletedWhileExpandedLosesItsRowsAndItsEntry()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("a"), Dir("b"))
            .With(At("a"), File("x.txt"))
            .With(At("b"), File("y.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("a"));
        Assert.Equal(["a", "x.txt", "b"], Names(tree));

        fs.With(Root, Dir("b"));
        fs.Directories.Remove(At("a"));
        tree.Refresh();

        Assert.Equal(["b"], Names(tree));
        Assert.Empty(tree.ExpandedPaths);
    }

    [Fact]
    public void ADirectoryRenamedWhileExpandedOpensClosedUnderItsNewName()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("old"))
            .With(At("old"), File("x.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("old"));
        Assert.Equal(["old", "x.txt"], Names(tree));

        fs.With(Root, Dir("new"));
        fs.Directories.Remove(At("old"));
        fs.With(At("new"), File("x.txt"));
        tree.Refresh();

        var row = Assert.IsType<FileBrowserRow.Directory>(Assert.Single(tree.Rows));
        Assert.Equal("new", row.Name);
        Assert.False(row.IsExpanded);
        Assert.Empty(tree.ExpandedPaths);
    }

    [Fact]
    public void ACollapsedAncestorKeepsItsDescendantsExpandedState()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir("a"))
            .With(At("a"), Dir("b"))
            .With(At("a", "b"), File("x.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("a"));
        tree.Expand(At("a", "b"));
        tree.Collapse(At("a"));
        tree.Refresh();

        Assert.Contains(At("a", "b"), tree.ExpandedPaths);

        tree.Expand(At("a"));
        Assert.Equal(["a", "b", "x.txt"], Names(tree));
    }

    [Fact]
    public void AnUnreadableDirectoryDrawsNoChildrenAndKeepsTheRestOfTheTree()
    {
        var fs = new FakeFileSystem().With(Root, Dir("locked"), File("a.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("locked"));

        Assert.Equal(["locked", "a.txt"], Names(tree));
    }

    [Fact]
    public void TheGitDirectoryIsNeverListedAtAnyLevel()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir(".git"), Dir("nested"), File("a.txt"))
            .With(At("nested"), Dir(".git"), File("b.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);
        tree.Expand(At("nested"));

        Assert.Equal(["nested", "b.txt", "a.txt"], Names(tree));
    }

    [Fact]
    public void AGitDirectoryCasedDifferentlyIsStillNeverListed()
    {
        var fs = new FakeFileSystem()
            .With(Root, Dir(".Git"), Dir(".GIT"), File("a.txt"));

        var tree = new FileBrowserTree(fs, NoIgnoreOracle.Instance, Root);

        Assert.Equal(["a.txt"], Names(tree));
    }

    [Fact]
    public void IgnoredAndHiddenEntriesAreMarkedNotRemoved()
    {
        var oracle = new FakeIgnoreOracle();
        oracle.IgnoredPaths.Add("build/");
        var fs = new FakeFileSystem()
            .With(Root, Dir("build"), Dir("src"), File(".env", isHidden: true));

        var tree = new FileBrowserTree(fs, oracle, Root);

        Assert.Equal(["build", "src", ".env"], Names(tree));
        Assert.True(tree.Rows[0].IsIgnored);
        Assert.False(tree.Rows[1].IsIgnored);
        Assert.True(tree.Rows[2].IsHidden);
    }

    [Fact]
    public void HidingDropsIgnoredAndHiddenRowsAndShowingBringsThemBack()
    {
        var oracle = new FakeIgnoreOracle();
        oracle.IgnoredPaths.Add("build/");
        var fs = new FakeFileSystem()
            .With(Root, Dir("build"), Dir("src"), File(".env", isHidden: true));

        var tree = new FileBrowserTree(fs, oracle, Root);
        tree.SetShowHidden(false);
        Assert.Equal(["src"], Names(tree));

        tree.SetShowHidden(true);
        Assert.Equal(["build", "src", ".env"], Names(tree));
    }

    [Fact]
    public void IgnoredNessIsInheritedWithoutAskingAgain()
    {
        var oracle = new FakeIgnoreOracle();
        oracle.IgnoredPaths.Add("build/");
        var fs = new FakeFileSystem()
            .With(Root, Dir("build"))
            .With(At("build"), File("out.o"));

        var tree = new FileBrowserTree(fs, oracle, Root);
        tree.Expand(At("build"));

        Assert.Equal(["build", "out.o"], Names(tree));
        Assert.True(tree.Rows[1].IsIgnored);
        Assert.Equal([["build/"]], oracle.Batches);
    }
}
