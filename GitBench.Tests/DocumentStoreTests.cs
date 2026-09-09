using GitBench.Features.Editor;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>What a buffer has to survive a re-read of the file it was opened from.</summary>
public class DocumentStoreTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-documents-");

    public void Dispose() => _dir.Dispose();

    private static readonly FileWriteBack Writable =
        new FileWriteBack.Reversible(new FileEncoding(FileCharset.Utf8, LineEnding.Lf, EndsWithNewline: true));

    [Fact]
    public void TheSameReadAnswersWithTheSameSession()
    {
        var documents = TestDocuments.ForOneRepo();
        var lines = Lines("one", "two");

        var first = documents.Open(Path, lines, Writable, null);
        var second = documents.Open(Path, lines, Writable, null);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void ARereadOfAFileBeingTypedIntoAnswersWithWhatWasTypedIntoIt()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one", "two"), Writable, null)!;
        Type(buffer, "x");

        var reread = documents.Open(Path, Lines("something", "else"), Writable, null);

        Assert.Same(buffer, reread);
        Assert.Equal("xone", buffer.Session.Document.Line(new FileLine(1)));
    }

    [Fact]
    public void ARereadOfAFileNothingWasTypedIntoAnswersWithTheNewText()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one", "two"), Writable, null)!;

        var reread = documents.Open(Path, Lines("something", "else"), Writable, null)!;

        Assert.NotSame(buffer, reread);
        Assert.Equal("something", reread.Session.Document.Line(new FileLine(1)));
    }

    /// <summary>
    /// A save is written and read back, and the read that comes back describes the document again.
    /// Everything computed from a read is drawn only while that holds — the parse that colors the
    /// file, the find bar's hits, a language server's diagnostics — so measuring it against the
    /// revision the buffer opened at hid all three from the first keystroke until the file was
    /// closed, saving included.
    /// </summary>
    [Fact]
    public void SavingAndRereadingMakesTheReadDescribeTheDocumentAgain()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one", "two"), Writable, null)!;
        Assert.True(buffer.ReadIsCurrent);

        Type(buffer, "x");
        Assert.False(buffer.ReadIsCurrent);

        documents.MarkSaved(Path);
        var reread = documents.Open(Path, Lines("xone", "two"), Writable, null);

        Assert.Same(buffer, reread);
        Assert.True(buffer.ReadIsCurrent);
    }

    [Fact]
    public void AFileThatCannotBeWrittenBackIsNotEditable()
    {
        var documents = TestDocuments.ForOneRepo();

        var truncated = documents.Open(
            Path, Lines("one"), new FileWriteBack.Refused(WriteBackRefusal.Truncated), null);

        Assert.Null(truncated);
        Assert.Empty(documents.Unsaved());
    }

    [Fact]
    public void UnsavedFollowsTheDocumentRatherThanAFlag()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one"), Writable, null)!;

        Assert.False(documents.HasUnsavedEdits(Path));

        Type(buffer, "x");
        Assert.True(documents.HasUnsavedEdits(Path));
        Assert.Equal([PathKey.Normalize(Path)], documents.Unsaved());

        documents.MarkSaved(Path);
        Assert.False(documents.HasUnsavedEdits(Path));
    }

    [Fact]
    public void AnUndoBackToTheFileOnDiskDeliberatelyStillReadsAsUnsaved()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one"), Writable, null)!;

        Type(buffer, "x");
        buffer.Session.Undo();

        Assert.Equal("one", buffer.Session.Document.Line(new FileLine(1)));
        Assert.True(documents.HasUnsavedEdits(Path));
    }

    [Fact]
    public void ClosingAFileForgetsWhatWasTypedIntoIt()
    {
        var documents = TestDocuments.ForOneRepo();
        var buffer = documents.Open(Path, Lines("one"), Writable, null)!;
        Type(buffer, "x");

        documents.Close(Path);

        Assert.False(documents.HasUnsavedEdits(Path));
        var reopened = documents.Open(Path, Lines("one"), Writable, null);
        Assert.NotSame(buffer, reopened);
    }

    [Fact]
    public void UnsavedAnswersForEveryRepositoryAtOnce()
    {
        using var registry = Registry();
        var one = OpenRepo(registry, "one");
        var two = OpenRepo(registry, "two");
        using var store = new DocumentStore(registry, Localization());
        store.Start();

        Type(store.For(one).Open(Path, Lines("one"), Writable, null)!, "x");
        store.For(two).Open(Other, Lines("two"), Writable, null);

        Assert.Equal([new UnsavedFile(one, PathKey.Normalize(Path))], store.Unsaved());
    }

    [Fact]
    public void ARepositoryLeavingTheSidebarTakesItsBuffersWithItOnceRemovalIsAgreedTo()
    {
        using var registry = Registry();
        var repo = OpenRepo(registry, "one");
        using var store = new DocumentStore(registry, Localization());
        store.Start();
        Type(store.For(repo).Open(Path, Lines("one"), Writable, null)!, "x");

        registry.RemoveRepo(repo);

        Assert.Empty(store.Unsaved());
    }

    [Fact]
    public void TypingIntoAFileIsAnnouncedForThatPathAndNoOther()
    {
        using var registry = Registry();
        var repo = OpenRepo(registry, "one");
        using var store = new DocumentStore(registry, Localization());
        store.Start();
        var edited = new List<string>();
        store.Edited += edited.Add;

        var buffer = store.For(repo).Open(Path, Lines("one"), Writable, null)!;
        store.For(repo).Open(Other, Lines("two"), Writable, null);
        Type(buffer, "x");

        Assert.Equal([PathKey.Normalize(Path)], edited);
    }

    [Fact]
    public void OnlyAFileWithSomethingTypedIntoItAnswersWithItsText()
    {
        using var registry = Registry();
        var repo = OpenRepo(registry, "one");
        using var store = new DocumentStore(registry, Localization());
        store.Start();
        var buffer = store.For(repo).Open(Path, Lines("one"), Writable, null)!;

        Assert.Null(store.UnsavedTextAt(Path));

        Type(buffer, "x");
        Assert.Equal(buffer.Session.Document.Text, store.UnsavedTextAt(Path));
        Assert.StartsWith("xone", store.UnsavedTextAt(Path)!, StringComparison.Ordinal);

        store.MarkSaved(Path);
        Assert.Null(store.UnsavedTextAt(Path));
    }

    [Fact]
    public void ReadingEditedFilesOffTheThreadThatOpenedThemIsRefused()
    {
        var documents = TestDocuments.ForOneRepo();
        documents.Open(Path, Lines("one"), Writable, null);

        Exception? offThread = null;
        var reader = new Thread(() => offThread = Record.Exception(() => documents.UnsavedTextAt(Path)));
        reader.Start();
        reader.Join();

        Assert.IsType<InvalidOperationException>(offThread);
    }

    private static string Path => OperatingSystem.IsWindows() ? @"C:\repo\one.txt" : "/repo/one.txt";

    private static string Other => OperatingSystem.IsWindows() ? @"C:\repo\two.txt" : "/repo/two.txt";

    private static FileText Lines(params string[] lines) => FilePreviewFixture.Of(lines);

    private static void Type(EditorBuffer buffer, string text) =>
        buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), text);

    private static ILocalizationService Localization() =>
        new LocalizationService(new State<Locale>(Locale.En));

    private RepoRegistry Registry()
    {
        var statePath = System.IO.Path.Combine(_dir.Path, "state.json");
        return new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    private Guid OpenRepo(RepoRegistry registry, string name)
    {
        var path = System.IO.Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(System.IO.Path.Combine(path, ".git"));
        registry.Open(path);
        return registry.Active.Value!.Id;
    }
}
