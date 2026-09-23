using System.Collections.Concurrent;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Infrastructure;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>Where a language server's copy of a file comes from once the file can be typed into.</summary>
public sealed class LanguageServerFileTextTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-lsp-filetext-");
    private readonly TextSession _server = new();
    private readonly ScriptedText _text = new();
    private readonly ManualDelays _delays = new();
    private readonly string _file;

    public LanguageServerFileTextTests()
    {
        _file = Path.Combine(_dir.Path, "main.rs");
        File.WriteAllText(_file, "fn main() {}");
        _text[_file] = "fn main() {}";
    }

    public void Dispose() => _dir.Dispose();

    private LanguageServerConnection Connect(IFileTextSource? files = null) => new(
        _server,
        new ServerLaunchRequest(Entry(), _dir.Path, _dir.Path),
        TimeSpan.FromSeconds(5),
        wait: _delays.Wait,
        files: files ?? _text);

    private static LanguageServerEntry Entry() => new(
        LanguageId.Of("rust"),
        "rust-analyzer",
        Args: [],
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(5),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private Task<HoverText?> Hover(LanguageServerConnection connection) =>
        connection.HoverAsync(_file, new FileLine(1), new RawColumn(3), CancellationToken.None);

    [Fact]
    public async Task AHoverAfterThreeEditsResolvesAgainstTheEditedText()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Edit(_file, "fn main() { one(); }");
        _text.Edit(_file, "fn main() { one(); two(); }");
        _text.Edit(_file, "fn main() { one(); two(); three(); }");

        var hover = await Hover(connection);

        Assert.Equal("fn main() { one(); two(); three(); }", hover!.Markdown);
        Assert.Equal("fn main() { one(); two(); three(); }", _server.Opened[^1].Text);
    }

    [Fact]
    public async Task ReopeningAnEditedFileGivesItANewVersion()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Edit(_file, "fn main() { edited(); }");
        await Hover(connection);

        Assert.Equal(2, _server.Opened.Count);
        Assert.True(_server.Opened[1].Version.Value > _server.Opened[0].Version.Value);
        Assert.Equal([DocumentUri.OfFile(_file)], _server.Closed);
    }

    [Fact]
    public async Task ABurstOfEditsIsOneReopen()
    {
        using var connection = Connect();
        await Hover(connection);

        for (var n = 1; n <= 6; n++) _text.Edit(_file, $"fn main() {{ edit{n}(); }}");

        Assert.Equal(6, _delays.Started);
        Assert.Single(_server.Opened);

        await Resynced(connection, _delays);

        Assert.Equal(2, _server.Opened.Count);
        Assert.Equal("fn main() { edit6(); }", _server.Opened[^1].Text);
    }

    [Fact]
    public async Task EditsEitherSideOfAPauseAreTwoReopens()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Edit(_file, "one");
        _text.Edit(_file, "two");
        await Resynced(connection, _delays);

        _text.Edit(_file, "three");
        await Resynced(connection, _delays);

        Assert.Equal(3, _server.Opened.Count);
        Assert.Equal("three", _server.Opened[^1].Text);
    }

    [Fact]
    public async Task TheDebounceIsThreeTenthsOfASecond()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Edit(_file, "edited");

        Assert.Equal(TimeSpan.FromMilliseconds(300), Assert.Single(_delays.Delays));
    }

    [Fact]
    public async Task AServerThatFollowsEditsIsSentTheEditRatherThanAReopen()
    {
        _server.Capabilities = _server.Capabilities! with { TextSync = TextSync.Incremental };
        using var connection = Connect();
        await Hover(connection);

        _text.Edit(_file, "fn main() { edited(); }");
        var hover = await Hover(connection);

        Assert.Single(_server.Opened);
        Assert.Empty(_server.Closed);
        Assert.Equal("fn main() { edited(); }", Assert.Single(_server.Changed).Text);
        Assert.Equal("fn main() { edited(); }", hover!.Markdown);
    }

    [Fact]
    public async Task AFileNobodyHasEditedIsReadOnceHoweverManyQuestionsAreAsked()
    {
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection);
        await Hover(connection);

        Assert.Equal(1, _text.Reads(_file));
        Assert.Single(_server.Opened);
    }

    [Fact]
    public async Task TextTheSeamRefusesToReadLeavesTheOpenDocumentAlone()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Lose(_file);
        var hover = await Hover(connection);

        Assert.Equal("fn main() {}", hover!.Markdown);
        Assert.Single(_server.Opened);
    }

    [Fact]
    public async Task TruncatedTextIsStillRefused()
    {
        _text.Truncate(_file);
        using var connection = Connect();

        var hover = await Hover(connection);

        Assert.Null(hover);
        Assert.Empty(_server.Opened);
        Assert.IsType<DocumentState.Truncated>(connection.Document);
    }

    [Fact]
    public async Task AFileEditedPastTheCutOffIsClosedRatherThanSentTruncated()
    {
        using var connection = Connect();
        await Hover(connection);

        _text.Truncate(_file);
        _text.Raise(_file);
        await Resynced(connection, _delays);

        Assert.Single(_server.Opened);
        Assert.Equal([DocumentUri.OfFile(_file)], _server.Closed);
        Assert.IsType<DocumentState.Truncated>(connection.Document);
    }

    [Fact]
    public async Task APathWithNoOpenDocumentIsReadFromDisk()
    {
        var text = await FilesOnDisk.Instance.ReadAsync(_file, CancellationToken.None);

        Assert.Equal(
            File.ReadAllText(_file),
            Assert.IsType<CurrentText.Complete>(text).Text);
    }

    [Fact]
    public async Task APathThatIsNotThereReadsAsNothingRatherThanThrowing()
    {
        var missing = Path.Combine(_dir.Path, "no-such-file.rs");

        var text = await FilesOnDisk.Instance.ReadAsync(missing, CancellationToken.None);

        Assert.IsType<CurrentText.Unavailable>(text);
    }

    [Fact]
    public async Task AFilePastTheCutOffReadsAsTruncatedRatherThanAsItsFirstTwoMegabytes()
    {
        var huge = Path.Combine(_dir.Path, "huge.rs");
        File.WriteAllBytes(huge, new byte[FileContentLoader.MaxTextBytes + 1]);

        var text = await FilesOnDisk.Instance.ReadAsync(huge, CancellationToken.None);

        Assert.IsType<CurrentText.CutShort>(text);
    }

    [Fact]
    public async Task UsagesSnippetsSplitLinesTheWayTheRestOfTheAppDoes()
    {
        const string oldMac = "fn one() {}\rfn two() {}\rfn three() {}";
        var path = Path.Combine(_dir.Path, "old-mac.rs");
        _text[path] = oldMac;

        var usages = await Usages.From(
            _dir.Path,
            [new DefinitionTarget.OutsideRepo(path, At(2)), new DefinitionTarget.OutsideRepo(path, At(3))],
            _text,
            CancellationToken.None);

        Assert.Equal(
            [TextLines.Split(oldMac)[1], TextLines.Split(oldMac)[2]],
            Usages.SitesOf(usages).Select(site => Assert.IsType<UsageText.Source>(site.Text).Text));
    }

    [Fact]
    public async Task AUsageInAFileBeingEditedQuotesTheEditedLine()
    {
        var path = Path.Combine(_dir.Path, "edited.rs");
        _text[path] = "fn was() {}";
        _text.Edit(path, "fn is() {}");

        var usages = await Usages.From(
            _dir.Path, [new DefinitionTarget.OutsideRepo(path, At(1))], _text, CancellationToken.None);

        Assert.Equal(
            "fn is() {}",
            Assert.IsType<UsageText.Source>(Assert.Single(Usages.SitesOf(usages)).Text).Text);
    }

    [Fact]
    public async Task AHoverOnAFileBeingTypedIntoAnswersAboutWhatWasTyped()
    {
        using var ui = new UiThread();
        var documents = new TestDocuments.Empty();
        var buffer = ui.Run(() => Open(documents, _file, "fn main() {}"));
        ui.Run(() => buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// "));

        using var connection = Connect(new DocumentBackedText(documents, ui));
        var hover = await Hover(connection);

        Assert.Equal("// fn main() {}", hover!.Markdown);
        Assert.NotEqual(File.ReadAllText(_file), hover.Markdown);
    }

    [Fact]
    public async Task AFileOpenButNotTypedIntoIsReadFromDiskByteForByte()
    {
        using var ui = new UiThread();
        var documents = new TestDocuments.Empty();
        ui.Run(() => Open(documents, _file, "fn main() {}"));
        File.WriteAllText(_file, "fn main() {}\n// written underneath\n");

        var text = await new DocumentBackedText(documents, ui).ReadAsync(_file, CancellationToken.None);

        Assert.Equal(File.ReadAllText(_file), Assert.IsType<CurrentText.Complete>(text).Text);
    }

    [Fact]
    public async Task APathWithNoDocumentOpenOnItIsReadFromDisk()
    {
        using var ui = new UiThread();

        var text = await new DocumentBackedText(new TestDocuments.Empty(), ui)
            .ReadAsync(_file, CancellationToken.None);

        Assert.Equal(File.ReadAllText(_file), Assert.IsType<CurrentText.Complete>(text).Text);
    }

    [Fact]
    public async Task AReadFromAPoolThreadCompletesWhileTheUiThreadIsPumping()
    {
        using var ui = new UiThread();
        var documents = new TestDocuments.Empty();
        var buffer = ui.Run(() => Open(documents, _file, "fn main() {}"));
        ui.Run(() => buffer.Session.Type(SelectionRange.At(TextPosition.At(1, 0)), "// "));
        var files = new DocumentBackedText(documents, ui);

        var text = await Task.Run(() => files.ReadAsync(_file, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("// fn main() {}", Assert.IsType<CurrentText.Complete>(text).Text);
    }

    private static EditorBuffer Open(IDocumentStore documents, string path, params string[] lines) =>
        documents.For(Guid.NewGuid()).Open(
            path,
            FilePreviewFixture.Of(lines, endsWithNewline: false),
            FilePreviewFixture.Reversible,
            null)!;

    /// <summary>A thread that drains posted work the way the application's UI thread does.</summary>
    private sealed class UiThread : IUiDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _posted = new();
        private readonly Thread _thread;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _posted.GetConsumingEnumerable()) action();
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Post(Action action) => _posted.Add(action);

        public T Run<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });
            return done.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _posted.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task Resynced(LanguageServerConnection connection, ManualDelays delays)
    {
        var published = new TaskCompletionSource<DocumentState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action<DocumentState> capture = state => published.TrySetResult(state);
        connection.DocumentChanged += capture;
        try
        {
            delays.Advance();
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            connection.DocumentChanged -= capture;
        }
    }

    private static LspPosition At(int oneBasedLine) =>
        new(LspLine.FromOneBased(oneBasedLine), new LspCharacter(0));

    private sealed class ScriptedText : IFileTextSource
    {
        private readonly Dictionary<string, CurrentText> _paths = [];
        private readonly Dictionary<string, int> _reads = [];

        public event Action<string>? Changed;

        public string this[string path]
        {
            set => _paths[path] = CurrentText.Whole(value);
        }

        public int Reads(string path) => _reads.TryGetValue(path, out var n) ? n : 0;

        public void Edit(string path, string text)
        {
            this[path] = text;
            Raise(path);
        }

        public void Lose(string path) => _paths[path] = CurrentText.Nothing;

        public void Truncate(string path) => _paths[path] = CurrentText.Truncated;

        public void Raise(string path) => Changed?.Invoke(path);

        public Task<CurrentText> ReadAsync(string absolutePath, CancellationToken cancel)
        {
            _reads[absolutePath] = Reads(absolutePath) + 1;
            return Task.FromResult(
                _paths.TryGetValue(absolutePath, out var text) ? text : CurrentText.Nothing);
        }
    }

    private sealed class ManualDelays
    {
        private readonly List<TaskCompletionSource> _outstanding = [];

        public List<TimeSpan> Delays { get; } = [];

        public int Started => Delays.Count;

        public Task Wait(TimeSpan delay, CancellationToken cancel)
        {
            Delays.Add(delay);
            var due = new TaskCompletionSource();
            cancel.Register(() => due.TrySetCanceled());
            _outstanding.Add(due);
            return due.Task;
        }

        public void Advance()
        {
            var due = _outstanding.ToArray();
            _outstanding.Clear();
            foreach (var wait in due) wait.TrySetResult();
        }
    }

    private sealed class TextSession : ILanguageServerSession
    {
        private readonly Dictionary<DocumentUri, string> _documents = [];

        public List<(DocumentUri Uri, DocumentVersion Version, string Text)> Opened { get; } = [];

        public List<DocumentUri> Closed { get; } = [];

        public List<(DocumentUri Uri, DocumentVersion Version, string Text)> Changed { get; } = [];

        public event Action<ServerReadiness>? ReadinessChanged;

        public event Action<ServerExit>? Exited;

        public event Action<PublishedDiagnostics>? DiagnosticsPublished;

        public ServerCapabilities? Capabilities { get; set; } = new(
            ServerName: "fake",
            ServerCapabilities.Utf16,
            SupportsHover: true,
            SupportsDefinition: true,
            SupportsReferences: true);

        public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel) =>
            Task.FromResult<string?>(null);

        public Task OpenAsync(
            DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel)
        {
            Opened.Add((uri, version, text));
            _documents[uri] = text;
            return Task.CompletedTask;
        }

        public Task ChangeAsync(DocumentUri uri, DocumentVersion version, string text, CancellationToken cancel)
        {
            Changed.Add((uri, version, text));
            _documents[uri] = text;
            return Task.CompletedTask;
        }

        public Task CloseAsync(DocumentUri uri, CancellationToken cancel)
        {
            Closed.Add(uri);
            _documents.Remove(uri);
            return Task.CompletedTask;
        }

        public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel)
        {
            var held = _documents.Values.FirstOrDefault() ?? string.Empty;
            var answer = new LspResponse<Hover>.Ok(new Hover.Text(MarkupKind.Markdown, held, null));
            return Task.FromResult((LspResponse<T>)(object)answer);
        }

        public void Report(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

        public void Publish(PublishedDiagnostics published) => DiagnosticsPublished?.Invoke(published);

        public void RequestShutdown() => Exited?.Invoke(new ServerExit(0));

        public void Kill() => Exited?.Invoke(new ServerExit());

        public void Dispose() => _documents.Clear();
    }
}
