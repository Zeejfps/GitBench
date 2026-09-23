using GitBench.Features.CodeIntel;
using GitBench.Features.Search;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests.Search;

/// <summary>The symbol index over a temp repository: building it, and keeping it in step with edits,
/// deletions and renames without parsing what has not changed.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class RepoSymbolIndexTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-symbol-index-");
    private readonly GitService _git = new(new NullActivityTracker());
    private readonly Repo _repo;
    private readonly CountingExtractor _extractor;
    private readonly RepoSymbolIndex _index;
    private readonly List<SymbolIndexSnapshot> _published = new();

    public RepoSymbolIndexTests(CodeIntelFixture codeIntel)
    {
        TestGit.Init(_dir.Path);
        _repo = new Repo(Guid.NewGuid(), _dir.Path, "index");
        _extractor = new CountingExtractor(codeIntel.Extractor);
        _index = new RepoSymbolIndex(_repo.Id, _dir.Path, _extractor, parallelism: 2);
    }

    public void Dispose() => _dir.Dispose();

    private void Write(string path, string text)
    {
        var full = Path.Combine(_dir.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private SymbolIndexSnapshot Refresh()
    {
        _published.Clear();
        _index.Refresh(_git.ListWorkingTreeFiles(_repo), _published.Add, CancellationToken.None);
        return _published[^1];
    }

    private static IEnumerable<(string Name, string? Container, string Path)> Names(SymbolIndexSnapshot snapshot) =>
        snapshot.Symbols.Select(s => (s.Name, s.Container, s.Path)).OrderBy(s => s.Path).ThenBy(s => s.Name);

    [Fact]
    public void ABuild_ListsTheDeclarationsOfEverySourceFile_WithTheirContainers()
    {
        Write("src/Alpha.cs", "namespace App;\n\nclass Alpha\n{\n    void Run() { }\n}\n");
        Write("tools/beta.py", "def beta():\n    pass\n");

        var snapshot = Refresh();

        Assert.IsType<SymbolIndexProgress.Complete>(snapshot.Progress);
        Assert.Equal(
            [("Alpha", null, "src/Alpha.cs"), ("Run", "Alpha", "src/Alpha.cs"), ("beta", null, "tools/beta.py")],
            Names(snapshot));

        var run = snapshot.Symbols.Single(s => s.Name == "Run");
        Assert.Equal(5, run.Line.Value);
        Assert.Equal(9, run.Column.Value);
    }

    [Fact]
    public void LargeBinaryAndNonCodeFiles_AreLeftOut()
    {
        Write("Keep.cs", "class Keep { }\n");
        Write("Huge.cs", "class Huge { }\n" + new string('/', (int)RepoSymbolIndex.MaxFileBytes));
        Write("Binary.cs", "class Binary { }\0\0\0");
        Write("notes.md", "# A heading\n");
        Write("package.json", "{ \"name\": \"x\" }\n");

        var snapshot = Refresh();

        Assert.Equal([("Keep", null, "Keep.cs")], Names(snapshot));
    }

    [Fact]
    public void AnUnchangedFile_IsNotParsedAgain()
    {
        Write("A.cs", "class A { }\n");
        Write("B.cs", "class B { }\n");
        Refresh();
        var parsed = _extractor.Calls;

        Refresh();

        Assert.Equal(parsed, _extractor.Calls);
    }

    [Fact]
    public void AnEditedFile_IsParsedAgain()
    {
        Write("A.cs", "class A { }\n");
        Write("B.cs", "class B { }\n");
        Refresh();
        var parsed = _extractor.Calls;

        Write("A.cs", "class Renamed { void Go() { } }\n");
        var snapshot = Refresh();

        Assert.Equal(parsed + 1, _extractor.Calls);
        Assert.Equal([("B", null, "B.cs"), ("Go", "Renamed", "A.cs"), ("Renamed", null, "A.cs")],
            Names(snapshot).OrderBy(n => n.Name));
    }

    [Fact]
    public void ADeletedFile_LeavesTheIndex()
    {
        Write("A.cs", "class A { }\n");
        Write("B.cs", "class B { }\n");
        Refresh();

        File.Delete(Path.Combine(_dir.Path, "B.cs"));
        var snapshot = Refresh();

        Assert.Equal([("A", null, "A.cs")], Names(snapshot));
    }

    [Fact]
    public void ARenamedFile_IsFoundAtItsNewPath()
    {
        Write("A.cs", "class A { }\n");
        Refresh();

        Directory.CreateDirectory(Path.Combine(_dir.Path, "moved"));
        File.Move(Path.Combine(_dir.Path, "A.cs"), Path.Combine(_dir.Path, "moved", "A.cs"));
        var snapshot = Refresh();

        Assert.Equal([("A", null, "moved/A.cs")], Names(snapshot));
    }

    [Fact]
    public void ProgressIsPublishedAsFilesLand_ThenComplete()
    {
        for (var i = 0; i < 12; i++) Write($"F{i}.cs", $"class F{i} {{ }}\n");

        Refresh();

        var building = Assert.IsType<SymbolIndexProgress.Building>(_published[0].Progress);
        Assert.Equal((0, 12), (building.Done, building.Total));
        Assert.IsType<SymbolIndexProgress.Complete>(_published[^1].Progress);
        Assert.Equal(12, _published[^1].Symbols.Count());
    }

    [Fact]
    public void ARefreshWithNothingChanged_PublishesOnlyComplete()
    {
        Write("A.cs", "class A { }\n");
        Refresh();

        Refresh();

        Assert.IsType<SymbolIndexProgress.Complete>(Assert.Single(_published).Progress);
    }

    private sealed class CountingExtractor(ISymbolExtractor inner) : ISymbolExtractor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public CodeIntelAvailability Availability => inner.Availability;

        public FileOutline? Extract(string text, CodeLanguage language)
        {
            Interlocked.Increment(ref _calls);
            return inner.Extract(text, language);
        }
    }
}
