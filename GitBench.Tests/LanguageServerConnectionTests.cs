using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The app's end of one running server: the opening exchange, the file it is told about once, and
/// the question asked again while it says it is still indexing. Driven by a fake server, so none of
/// this needs a subprocess.
/// </summary>
public sealed class LanguageServerConnectionTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-lsp-connection-");
    private readonly FakeSession _server = new();
    private readonly List<TimeSpan> _waited = [];
    private readonly string _file;

    public LanguageServerConnectionTests()
    {
        _file = Path.Combine(_dir.Path, "main.rs");
        File.WriteAllText(_file, "fn main() {}");
    }

    public void Dispose() => _dir.Dispose();

    private LanguageServerConnection Connect(string? projectRoot = null, string? repoRoot = null) => new(
        _server,
        new ServerLaunchRequest(Entry(), projectRoot ?? _dir.Path, repoRoot ?? _dir.Path),
        TimeSpan.FromSeconds(5),
        AskAgainPolicy.Default with { MaxAttempts = 3 },
        (delay, _) =>
        {
            _waited.Add(delay);
            return Task.CompletedTask;
        });

    private static LanguageServerEntry Entry() => new(
        LanguageId.Of("rust"),
        "rust-analyzer",
        Args: [],
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        SettingsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(5),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private Task<HoverText?> Hover(LanguageServerConnection connection, string? path = null) =>
        connection.HoverAsync(path ?? _file, new FileLine(1), new RawColumn(3), CancellationToken.None);

    private static LspResponse<Hover> Answer(string markdown) =>
        new LspResponse<Hover>.Ok(new Hover.Text(MarkupKind.Markdown, markdown, null));

    private static LspResponse<Hover> NotReady() =>
        new LspResponse<Hover>.Retryable(LspErrorCode.ServerNotInitialized, "still indexing");

    [Fact]
    public async Task AHoverIsAnsweredOnceTheServerIsSpokenTo()
    {
        _server.Answers.Enqueue(Answer("`fn main()`"));
        using var connection = Connect();

        var hover = await Hover(connection);

        Assert.Equal("`fn main()`", hover!.Markdown);
        Assert.Equal(1, _server.Handshakes);
    }

    [Fact]
    public async Task TheFileIsOpenedOnceHoweverManyQuestionsAreAsked()
    {
        _server.Answers.Enqueue(Answer("one"));
        _server.Answers.Enqueue(Answer("two"));
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection);

        Assert.Single(_server.Opened);
        Assert.Equal(2, _server.Asks);
    }

    [Fact]
    public async Task ASecondFileIsOpenedInItsOwnRight()
    {
        var other = Path.Combine(_dir.Path, "lib.rs");
        File.WriteAllText(other, "pub fn lib() {}");
        _server.Answers.Enqueue(Answer("one"));
        _server.Answers.Enqueue(Answer("two"));
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection, other);

        Assert.Equal(2, _server.Opened.Count);
    }

    // The bug seen live: the first hover of a session lands while the project is still loading, and
    // one question asked once produces nothing at all.
    [Fact]
    public async Task AServerStillIndexingIsAskedAgain()
    {
        _server.Answers.Enqueue(NotReady());
        _server.Answers.Enqueue(Answer("`fn main()`"));
        using var connection = Connect();

        var hover = await Hover(connection);

        Assert.Equal("`fn main()`", hover!.Markdown);
        Assert.Equal(2, _server.Asks);
        Assert.Single(_waited);
    }

    [Fact]
    public async Task AServerStillIndexingAfterTheLastAttemptSaysNothing()
    {
        for (var i = 0; i < 5; i++) _server.Answers.Enqueue(NotReady());
        using var connection = Connect();

        Assert.Null(await Hover(connection));
        Assert.Equal(3, _server.Asks);
    }

    [Fact]
    public async Task AFailedHandshakeEndsTheConnectionWithWhatWentWrong()
    {
        _server.HandshakeFailure = "server counts positions as utf-8, which this client cannot address.";
        using var connection = Connect();

        Assert.Null(await Hover(connection));
        Assert.Equal(0, _server.Asks);
        Assert.Equal(1, _server.ShutdownRequests);
    }

    [Fact]
    public async Task AConnectionThatFailedItsHandshakeReportsTheReasonAsItsExit()
    {
        var ended = new TaskCompletionSource<ServerExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.HandshakeFailure = "no answer to the opening request within 30s.";
        using var connection = Connect();
        connection.Exited += exit => ended.TrySetResult(exit);

        await Hover(connection);

        var exit = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("no answer to the opening request within 30s.", exit.Detail);
    }

    // A file the preview cut short is not the file the server would read, so it is never sent and
    // never asked about.
    [Fact]
    public async Task AFileTooLargeForThePreviewIsNeverSent()
    {
        var huge = Path.Combine(_dir.Path, "huge.rs");
        File.WriteAllBytes(huge, new byte[FileContentLoader.MaxTextBytes + 1]);
        using var connection = Connect();

        Assert.Null(await Hover(connection, huge));
        Assert.Empty(_server.Opened);
        Assert.Equal(0, _server.Asks);
    }

    [Fact]
    public async Task AFileThatIsNotThereIsNeverSent()
    {
        using var connection = Connect();

        Assert.Null(await Hover(connection, Path.Combine(_dir.Path, "gone.rs")));
        Assert.Empty(_server.Opened);
    }

    [Fact]
    public async Task AnEmptyAnswerIsNoAnswerRatherThanAnEmptyPopup()
    {
        _server.Answers.Enqueue(new LspResponse<Hover>.Ok(new Hover.None()));
        using var connection = Connect();

        Assert.Null(await Hover(connection));
    }

    [Fact]
    public void ShutdownAndKillReachTheServerItself()
    {
        using var connection = Connect();

        connection.RequestShutdown();
        connection.Kill();

        Assert.Equal(1, _server.ShutdownRequests);
        Assert.True(_server.WasKilled);
    }

    [Fact]
    public void ReadinessTheServerReportsIsPassedOn()
    {
        using var connection = Connect();
        ServerReadiness? seen = null;
        connection.ReadinessChanged += readiness => seen = readiness;

        _server.Report(new ServerReadiness.Indexing(70));

        Assert.Equal(70, Assert.IsType<ServerReadiness.Indexing>(seen).PercentComplete);
    }

    // One ending per connection: a server that exits after its handshake already ended it must not
    // report a second crash for the same server.
    [Fact]
    public async Task AServerThatExitsAfterAFailedHandshakeEndsOnlyOnce()
    {
        _server.HandshakeFailure = "the server ended during startup: pipe closed";
        var exits = 0;
        using var connection = Connect();
        connection.Exited += _ => exits++;

        await Hover(connection);
        _server.End(new ServerExit(1));

        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task ShowingAnotherFileClosesTheOneBefore()
    {
        var other = Path.Combine(_dir.Path, "lib.rs");
        File.WriteAllText(other, "pub fn lib() {}");
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection, other);

        Assert.Equal(DocumentUri.OfFile(_file), Assert.Single(_server.Closed));
    }

    [Fact]
    public async Task DiagnosticsForTheOpenFileBecomeItsState()
    {
        using var connection = Connect();
        await Hover(connection);

        _server.Publish(Wave(_file, Problem("cannot find value `x`")));

        var open = Assert.IsType<DocumentState.Open>(connection.Document);
        var received = Assert.IsType<DiagnosticsState.Received>(open.Diagnostics);
        Assert.Equal("cannot find value `x`", Assert.Single(received.Diagnostics).Message);
    }

    // Waves replace: a server that re-checks a file sends the whole set again, and adding would
    // leave every fixed error on screen for as long as the file is open.
    [Fact]
    public async Task ALaterWaveReplacesTheOneBeforeItRatherThanAddingToIt()
    {
        using var connection = Connect();
        await Hover(connection);

        _server.Publish(Wave(_file, Problem("first"), Problem("second")));
        _server.Publish(Wave(_file, Problem("only")));

        var open = Assert.IsType<DocumentState.Open>(connection.Document);
        var received = Assert.IsType<DiagnosticsState.Received>(open.Diagnostics);
        Assert.Equal("only", Assert.Single(received.Diagnostics).Message);
    }

    [Fact]
    public async Task AnEmptyWaveMeansTheFileIsCleanRatherThanUnchecked()
    {
        using var connection = Connect();
        await Hover(connection);

        _server.Publish(Wave(_file));

        var open = Assert.IsType<DocumentState.Open>(connection.Document);
        Assert.Empty(Assert.IsType<DiagnosticsState.Received>(open.Diagnostics).Diagnostics);
    }

    [Fact]
    public async Task DiagnosticsForAFileThatIsNotOnScreenAreDropped()
    {
        using var connection = Connect();
        await Hover(connection);

        _server.Publish(Wave(Path.Combine(_dir.Path, "other.rs"), Problem("elsewhere")));

        var open = Assert.IsType<DocumentState.Open>(connection.Document);
        Assert.IsType<DiagnosticsState.Waiting>(open.Diagnostics);
    }

    [Fact]
    public async Task AFileWithNoDiagnosticsYetIsWaitingRatherThanClean()
    {
        using var connection = Connect();

        await Hover(connection);

        var open = Assert.IsType<DocumentState.Open>(connection.Document);
        Assert.IsType<DiagnosticsState.Waiting>(open.Diagnostics);
    }

    // A truncated file is never sent, so it has no diagnostics — and that is a different screen
    // from a file the server checked and found clean.
    [Fact]
    public async Task AFileTooLargeForThePreviewIsNotSentAndIsNotClean()
    {
        var huge = Path.Combine(_dir.Path, "huge.rs");
        File.WriteAllBytes(huge, new byte[FileContentLoader.MaxTextBytes + 1]);
        using var connection = Connect();

        await Hover(connection, huge);

        var skipped = Assert.IsType<DocumentState.NotSent>(connection.Document);
        Assert.Equal(SkipReason.PreviewTruncated, skipped.Reason);
    }

    [Fact]
    public async Task AFreshWaveIsAnnouncedSoThePaneCanRedraw()
    {
        using var connection = Connect();
        var changes = new List<DocumentState>();
        await Hover(connection);
        connection.DocumentChanged += changes.Add;

        _server.Publish(Wave(_file, Problem("boom")));

        Assert.Single(changes);
    }

    private async Task<IReadOnlyList<DefinitionTarget>> Define(
        LanguageServerConnection connection, string? path = null) =>
        (await Reply(connection, path)).Targets;

    private Task<DefinitionReply> Reply(LanguageServerConnection connection, string? path = null) =>
        connection.DefinitionAsync(path ?? _file, new FileLine(9), new RawColumn(3), CancellationToken.None);

    private static LspResponse<Definition> Declares(string path, int nameLine, int bodyLine) =>
        new LspResponse<Definition>.Ok(new Definition.Targets([
            new DefinitionLocation(
                DocumentUri.OfFile(path),
                LspRange.Empty(new LspPosition(new LspLine(nameLine), new LspCharacter(4))),
                LspRange.Empty(new LspPosition(new LspLine(bodyLine), new LspCharacter(0)))),
        ]));

    [Fact]
    public async Task ADefinitionInTheRepositoryComesBackAsAPathTheTreeCanReach()
    {
        var target = Path.Combine(_dir.Path, "src", "lib.rs");
        _server.Definitions.Enqueue(Declares(target, nameLine: 41, bodyLine: 38));
        using var connection = Connect();

        var found = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(await Define(connection)));

        Assert.Equal("src/lib.rs", found.RelativePath.Replace('\\', '/'));
        Assert.Equal(new LspLine(41), found.Position.Line);
    }

    [Fact]
    public async Task TheLineAskedAboutIsTheOneBelowItInTheProtocol()
    {
        using var connection = Connect();

        await Define(connection);

        Assert.Equal(new LspLine(8), _server.LastPositionAsked!.Value.Line);
    }

    [Fact]
    public async Task ADefinitionOutsideTheRepositoryKeepsItsWholePath()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "std", "io.rs");
        _server.Definitions.Enqueue(Declares(outside, nameLine: 3, bodyLine: 1));
        using var connection = Connect();

        var found = Assert.IsType<DefinitionTarget.OutsideRepo>(Assert.Single(await Define(connection)));

        Assert.Equal(outside, found.AbsolutePath);
    }

    [Fact]
    public async Task ADefinitionIsMeasuredFromTheRepositoryRatherThanTheProjectRoot()
    {
        var project = Path.Combine(_dir.Path, "crate");
        Directory.CreateDirectory(project);
        var target = Path.Combine(project, "src", "lib.rs");
        _server.Definitions.Enqueue(Declares(target, nameLine: 2, bodyLine: 0));
        using var connection = Connect(projectRoot: project, repoRoot: _dir.Path);

        var found = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(await Define(connection)));

        Assert.Equal("crate/src/lib.rs", found.RelativePath.Replace('\\', '/'));
    }

    [Fact]
    public async Task ADefinitionUnderTheResolvedRepositoryPathIsStillInsideTheRepository()
    {
        var real = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(real, "src"));
        var link = Path.Combine(_dir.Path, "opened-through-here");
        if (!TryLink(link, real)) return;

        var target = Path.Combine(real, "src", "lib.rs");
        _server.Definitions.Enqueue(Declares(target, nameLine: 6, bodyLine: 4));
        using var connection = Connect(projectRoot: link, repoRoot: link);

        var found = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(await Define(connection)));

        Assert.Equal("src/lib.rs", found.RelativePath.Replace('\\', '/'));
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
        }
        catch (Exception)
        {
            return false;
        }

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        return Directory.Exists(link);
    }

    [Fact]
    public async Task AServerThatCannotGoToDefinitionIsNeverAsked()
    {
        _server.Capabilities = new ServerCapabilities(
            "fake", ServerCapabilities.Utf16, SupportsHover: true, SupportsDefinition: false);
        using var connection = Connect();

        Assert.Empty(await Define(connection));
        Assert.Equal(0, _server.DefinitionAsks);
    }

    [Fact]
    public async Task AServerThatFailedItsHandshakeIsNeverAskedForADefinition()
    {
        _server.HandshakeFailure = "'rust-analyzer' was not found.";
        using var connection = Connect();

        Assert.Empty(await Define(connection));
        Assert.Equal(0, _server.DefinitionAsks);
    }

    // The span the server resolved in the file being read, which is what the link under a held
    // modifier is drawn over. It crosses four types on its way up and is dropped by any of them
    // going quiet, so it is worth pinning end to end rather than only where it is parsed.
    [Fact]
    public async Task TheSpanAServerResolvedReachesTheCaller()
    {
        _server.Definitions.Enqueue(new LspResponse<Definition>.Ok(new Definition.Targets([
            new DefinitionLocation(
                DocumentUri.OfFile(_file),
                LspRange.Empty(new LspPosition(new LspLine(10), new LspCharacter(4))),
                LspRange.Empty(new LspPosition(new LspLine(10), new LspCharacter(0))),
                new LspRange(
                    new LspPosition(new LspLine(8), new LspCharacter(2)),
                    new LspPosition(new LspLine(8), new LspCharacter(7)))),
        ])));
        using var connection = Connect();

        var origin = Assert.IsType<OptionalRange.Present>((await Reply(connection)).Origin);

        Assert.Equal(new LspLine(8), origin.Range.Start.Line);
        Assert.Equal(new LspCharacter(2), origin.Range.Start.Character);
        Assert.Equal(new LspCharacter(7), origin.Range.End.Character);
    }

    [Fact]
    public async Task AServerThatSaidNoSuchSpanLeavesTheCallerToItsOwnWordScan()
    {
        _server.Definitions.Enqueue(Declares(_file, nameLine: 10, bodyLine: 9));
        using var connection = Connect();

        Assert.IsType<OptionalRange.NotGiven>((await Reply(connection)).Origin);
    }

    [Fact]
    public async Task ASymbolWithNoDefinitionIsNoTargetRatherThanAJumpToNowhere()
    {
        _server.Definitions.Enqueue(new LspResponse<Definition>.Ok(new Definition.None()));
        using var connection = Connect();

        Assert.Empty(await Define(connection));
    }

    [Fact]
    public async Task AskingAboutAFileOpensItFirst()
    {
        using var connection = Connect();

        await Define(connection);

        Assert.Equal(DocumentUri.OfFile(_file), Assert.Single(_server.Opened));
    }

    [Fact]
    public async Task AServerStillIndexingIsAskedForADefinitionAgain()
    {
        _server.Definitions.Enqueue(
            new LspResponse<Definition>.Retryable(LspErrorCode.ServerNotInitialized, "still indexing"));
        _server.Definitions.Enqueue(Declares(Path.Combine(_dir.Path, "lib.rs"), nameLine: 1, bodyLine: 0));
        using var connection = Connect();

        Assert.Single(await Define(connection));
        Assert.Equal(2, _server.DefinitionAsks);
    }

    private static PublishedDiagnostics Wave(string path, params Diagnostic[] items) =>
        new(DocumentUri.OfFile(path), ResultVersion.Untagged, items);

    private static Diagnostic Problem(string message) => new(
        new LspRange(new LspPosition(new LspLine(0), new LspCharacter(0)),
            new LspPosition(new LspLine(0), new LspCharacter(4))),
        DiagnosticSeverity.Error,
        message,
        Source: "rustc",
        Code: "E0425");

    private sealed class FakeSession : ILanguageServerSession
    {
        public readonly Queue<LspResponse<Hover>> Answers = new();
        public readonly List<DocumentUri> Opened = [];
        public readonly List<DocumentUri> Closed = [];

        public event Action<ServerReadiness>? ReadinessChanged;
        public event Action<ServerExit>? Exited;
        public event Action<PublishedDiagnostics>? DiagnosticsPublished;

        public string? HandshakeFailure { get; set; }

        public ServerCapabilities? Capabilities { get; set; } =
            new(ServerName: "fake", ServerCapabilities.Utf16, SupportsHover: true, SupportsDefinition: true);
        public int Handshakes { get; private set; }
        public int Asks { get; private set; }
        public int ShutdownRequests { get; private set; }
        public bool WasKilled { get; private set; }

        public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel)
        {
            Handshakes++;
            return Task.FromResult(HandshakeFailure);
        }

        public Task OpenAsync(
            DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel)
        {
            Opened.Add(uri);
            return Task.CompletedTask;
        }

        public readonly Queue<LspResponse<Definition>> Definitions = new();

        public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel)
        {
            Asks++;
            if (typeof(T) == typeof(Definition))
            {
                DefinitionAsks++;
                LastPositionAsked = PositionIn(request);
                var definition = Definitions.Count > 0
                    ? Definitions.Dequeue()
                    : new LspResponse<Definition>.Ok(new Definition.None());
                return Task.FromResult((LspResponse<T>)(object)definition);
            }

            var answer = Answers.Count > 0
                ? Answers.Dequeue()
                : new LspResponse<Hover>.Ok(new Hover.None());
            return Task.FromResult((LspResponse<T>)(object)answer);
        }

        public int DefinitionAsks { get; private set; }

        public LspPosition? LastPositionAsked { get; private set; }

        private static LspPosition? PositionIn<T>(LspRequest<T> request)
        {
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) request.WriteParams(writer);
            using var document = System.Text.Json.JsonDocument.Parse(stream.ToArray());
            var at = document.RootElement.GetProperty("position");
            return new LspPosition(
                new LspLine(at.GetProperty("line").GetInt32()),
                new LspCharacter(at.GetProperty("character").GetInt32()));
        }

        public void Report(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

        public void End(ServerExit exit) => Exited?.Invoke(exit);

        public void RequestShutdown() => ShutdownRequests++;

        public Task CloseAsync(DocumentUri uri, CancellationToken cancel)
        {
            Closed.Add(uri);
            return Task.CompletedTask;
        }

        public void Publish(PublishedDiagnostics published) => DiagnosticsPublished?.Invoke(published);

        public void Kill() => WasKilled = true;

        public void Dispose() { }
    }

    // The crash this prevents: the handshake fails on whatever pool thread was awaiting it, and
    // what listens for the ending rebuilds views. Raised there, the view tree is torn down while
    // the input system is walking it.
    [Fact]
    public async Task AnEndingIsAnnouncedOnTheThreadTheSupervisorRunsOn()
    {
        var posted = new List<Action>();
        using var connection = new LanguageServerConnection(
            _server,
            new ServerLaunchRequest(Entry(), _dir.Path, _dir.Path),
            TimeSpan.FromSeconds(5),
            post: posted.Add);
        var endings = 0;
        connection.Exited += _ => endings++;
        await Hover(connection);
        posted.Clear();

        _server.End(new ServerExit(1));

        Assert.Equal(0, endings);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, endings);
    }
}
