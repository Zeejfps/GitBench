using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Features.Repos;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The app's side of running servers: one repository's at a time, started by the first file that
/// wants one, and dropped when the repository goes away. Nothing here starts a process — the
/// launcher is a fake — so what is tested is the wiring, which is the part that had the bugs.
/// </summary>
public sealed class LanguageServerStoreTests : IDisposable
{
    private const string ConfigJson =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls",         "extensions": [".go"] }
          }
        }
        """;

    private readonly TempDir _dir = new("gitbench-lsp-store-");
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeFileSystem _files = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly List<ShowOperationErrorMessage> _errorDialogs = [];
    private readonly RepoRegistry _registry;
    private readonly string _configPath;
    private readonly Guid _first;
    private readonly Guid _second;

    private LanguageServerStore? _store;

    public LanguageServerStoreTests()
    {
        _configPath = Path.Combine(_dir.Path, "language-servers.json");
        _bus.Subscribe<ShowOperationErrorMessage>(_errorDialogs.Add);
        _registry = new RepoRegistry(RepoStateStore.Load(Path.Combine(_dir.Path, "state.json")),
            Path.Combine(_dir.Path, "state.json"));
        _first = OpenRepo("first", "Cargo.toml");
        _second = OpenRepo("second", "go.mod");
    }

    public void Dispose()
    {
        _store?.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private Guid OpenRepo(string name, string marker)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        Directory.CreateDirectory(Path.Combine(path, "src"));
        File.WriteAllText(Path.Combine(path, marker), "");
        File.WriteAllText(Path.Combine(path, "src", "main.rs"), "fn main() {}");
        _registry.Open(path);
        _files.With(Path.GetFullPath(path), marker, "src");
        return _registry.Active.Value!.Id;
    }

    private string File_(Guid repo, string relative) =>
        Path.Combine(Path.GetFullPath(_registry.Repos.Single(r => r.Id == repo).Path), relative);

    private LanguageServerStore Store(string? config = ConfigJson)
    {
        if (config is not null) File.WriteAllText(_configPath, config);
        var store = new LanguageServerStore(
            _registry,
            _files,
            _dispatcher,
            _bus,
            _loc,
            new LanguageServerLauncher(_launcher, TimeSpan.FromSeconds(5)),
            clock: null,
            configPath: _configPath);
        store.Start();
        _store = store;
        return store;
    }

    private static Task<HoverText?> Hover(LanguageServerStore store, string path) =>
        store.HoverAsync(
            Path.GetDirectoryName(path)!, path, new FileLine(1), new RawColumn(3), CancellationToken.None);

    private static async Task<IReadOnlyList<DefinitionTarget>> Define(
        LanguageServerStore store, string path) =>
        (await store.DefineAsync(path, new FileLine(4), new RawColumn(2), CancellationToken.None)).Targets;

    private static Definition DeclaredAt(string path, int line) =>
        new Definition.Targets([
            new DefinitionLocation(
                DocumentUri.OfFile(path),
                LspRange.Empty(new LspPosition(new LspLine(line), new LspCharacter(0))),
                LspRange.Empty(new LspPosition(new LspLine(line), new LspCharacter(0)))),
        ]);

    // A server that will not start is the one failure the reader can do something about — the
    // command is wrong, the binary is missing, the toolchain is not installed — and the reason is
    // the whole of the fix. It goes to the same dialog a failed git command goes to rather than
    // staying in a tooltip on a status dot, which is not somewhere anyone thinks to look.
    [Fact]
    public void AServerThatCannotStartRaisesTheErrorDialogWithTheReason()
    {
        _launcher.Refuse = "'rust-analyzer' was not found.";
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));

        var raised = Assert.Single(_errorDialogs);
        Assert.Equal("'rust-analyzer' was not found.", raised.Message);
        Assert.Contains("rust", raised.Title);
    }

    // A server can be dead before the supervisor has finished wiring it up: a binary that exits at
    // once, or a handshake refused in the first millisecond, as typescript-language-server does
    // when it cannot find a TypeScript to drive. The exit is replayed into the subscription rather
    // than arriving later, and it used to land on a half-built record and be discarded — leaving
    // the server sitting on "starting" with the real reason thrown away.
    [Fact]
    public void AServerThatDiesBeforeItIsWiredUpStillReportsWhy()
    {
        _launcher.FailHandshake = "the opening request was refused: no valid TypeScript installation.";
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));

        var raised = Assert.Single(_errorDialogs);
        Assert.Contains("no valid TypeScript installation", raised.Message);
    }

    [Fact]
    public void AServerThatDiesBeforeItIsWiredUpIsNotLeftLookingLikeItIsStarting()
    {
        _launcher.FailHandshake = "the opening request was refused: no valid TypeScript installation.";
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));

        Assert.IsType<ServerState.Failed>(
            store.Active.Value.StateFor(File_(_first, "src/main.rs")));
    }

    [Fact]
    public void AServerThatStartsRaisesNoDialog()
    {
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));

        Assert.Empty(_errorDialogs);
    }

    // The supervisor only reaches Failed once and leaves the server there until it is explicitly
    // retried, so browsing a tree full of files of that language cannot turn into a wall of
    // dialogs behind a server that will never start.
    [Fact]
    public void AFailedServerRaisesOneDialogHoweverManyFilesAreShown()
    {
        _launcher.Refuse = "'rust-analyzer' was not found.";
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));
        store.FileShown(File_(_first, "src/lib.rs"));
        store.FileShown(File_(_first, "src/other.rs"));

        Assert.Single(_errorDialogs);
    }

    [Fact]
    public async Task ADefinitionIsAskedOfTheServerThatClaimsTheFile()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        _launcher.Started.Single().Declares = DeclaredAt(File_(_first, "src/lib.rs"), line: 11);

        var found = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(await Define(store, file)));

        Assert.Equal("src/lib.rs", found.RelativePath.Replace('\\', '/'));
    }

    [Fact]
    public void AFileNoServerClaimsOffersNoDefinitionGesture()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.False(store.CanDefine(File_(_first, "README.md")));
        Assert.True(store.CanDefine(File_(_first, "src/main.rs")));
    }

    [Fact]
    public void AServerThatHasNotSpokenYetIsStillWorthAsking()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.True(store.CanDefine(File_(_first, "src/main.rs")));
    }

    [Fact]
    public async Task AServerThatSaysItCannotGoToDefinitionIsNotOffered()
    {
        var store = Store();
        _registry.SetActive(_first);
        _launcher.Advertise = new ServerCapabilities(
            "fake", ServerCapabilities.Utf16, SupportsHover: true, SupportsDefinition: false, SupportsReferences: false);
        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        await Hover(store, file);

        Assert.False(store.CanDefine(file));
        Assert.Empty(await Define(store, file));
        Assert.Equal(0, _launcher.Started.Single().DefinitionAsks);
    }

    [Fact]
    public async Task AFileOutsideTheActiveRepositoryHasNoServerToAskAbout()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.Empty(await Define(store, Path.Combine(_dir.Path, "nowhere", "main.rs")));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public void WithNoConfigFileNothingIsClaimedAndNothingRuns()
    {
        var store = Store(config: null);

        Assert.False(store.Handles(File_(_second, "src/main.rs")));
        Assert.False(store.Active.Value.ConfigFileExists);
        Assert.Empty(_launcher.Started);
    }

    // Opening the file is what starts the server, not asking it something. A cold project spends
    // tens of seconds indexing, and it should spend them while the file is being read.
    [Fact]
    public void ShowingAClaimedFileStartsItsServer()
    {
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "src/main.rs"));

        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task DiagnosticsForTheFileOnScreenReachThePane()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        await Hover(store, file);

        _launcher.Started.Single().Publish(Wave(file, "cannot find value `x`"));

        Assert.True(store.Diagnostics.Value.IsFor(file));
        Assert.Equal("cannot find value `x`", Assert.Single(store.Diagnostics.Value.Items).Message);
    }

    // A file with no server is not a file with no problems: nothing checked it.
    [Fact]
    public void AFileNoServerClaimsReportsNothingRatherThanACleanResult()
    {
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "README.md"));

        Assert.False(store.Diagnostics.Value.Answered);
        Assert.Empty(store.Diagnostics.Value.Items);
    }

    // Diagnostics belong to the file they were about. Moving to another one must not leave the
    // previous file's errors underlining this one's lines.
    [Fact]
    public async Task MovingToAnotherFileDropsTheDiagnosticsOfTheOneBefore()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        var other = Path.Combine(Path.GetDirectoryName(file)!, "lib.rs");
        File.WriteAllText(other, "pub fn lib() {}");
        store.FileShown(file);
        await Hover(store, file);
        _launcher.Started.Single().Publish(Wave(file, "boom"));

        store.FileShown(other);
        await Hover(store, other);

        Assert.False(store.Diagnostics.Value.IsFor(file));
        Assert.Empty(store.Diagnostics.Value.Items);
    }

    // The gap the pane actually lives in: a file goes on screen before the server has been told
    // about it, so for a moment the open document is still the file before. Its errors must not be
    // reported under the new file's name.
    [Fact]
    public async Task AFileJustPutOnScreenDoesNotInheritTheLastFilesErrors()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        var other = Path.Combine(Path.GetDirectoryName(file)!, "lib.rs");
        File.WriteAllText(other, "pub fn lib() {}");
        store.FileShown(file);
        await Hover(store, file);
        _launcher.Started.Single().Publish(Wave(file, "boom"));

        store.FileShown(other);

        Assert.Empty(store.Diagnostics.Value.Items);
        Assert.False(store.Diagnostics.Value.Answered);
    }

    // The crash this prevents: a wave arrives on a pool thread, the store publishes it there, and
    // the view tree rebuilds underneath the input system while it is walking its own controllers.
    // Everything else reaching this store is marshalled by the launcher; this path is the one that
    // has to marshal itself.
    [Fact]
    public async Task WhatTheViewTreeReadsIsOnlyEverWrittenOnTheUiThread()
    {
        var dispatcher = new DeferringDispatcher();
        File.WriteAllText(_configPath, ConfigJson);
        var store = new LanguageServerStore(
            _registry,
            _files,
            dispatcher,
            _bus,
            _loc,
            new LanguageServerLauncher(_launcher, TimeSpan.FromSeconds(5)),
            clock: null,
            configPath: _configPath);
        store.Start();
        _store = store;
        _registry.SetActive(_first);
        dispatcher.Drain();

        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        dispatcher.Drain();
        await DrainUntilDone(dispatcher, Hover(store, file));

        _launcher.Started.Single().Publish(Wave(file, "boom"));

        Assert.Empty(store.Diagnostics.Value.Items);
        dispatcher.Drain();
        Assert.Equal("boom", Assert.Single(store.Diagnostics.Value.Items).Message);
    }

    /// <summary>Plays the UI thread for a test: keeps draining while the work in flight is waiting
    /// on something that was posted to it.</summary>
    private static async Task DrainUntilDone(DeferringDispatcher dispatcher, Task work)
    {
        for (var i = 0; i < 500 && !work.IsCompleted; i++)
        {
            dispatcher.Drain();
            await Task.Yield();
            await Task.Delay(1);
        }

        dispatcher.Drain();
        await work;
    }

    // Ready means answered. A server started from the settings has no file being opened to do the
    // asking, so without this it indexes to 100% and then sits there looking stuck until someone
    // happens to hover over something.
    [Fact]
    public async Task AServerStartedByHandIsAskedAboutTheFileAlreadyOnScreen()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        await Hover(store, file);
        store.StopServer(LanguageId.Of("rust"));

        store.RetryServer(LanguageId.Of("rust"));

        var restarted = _launcher.Started[^1];
        for (var i = 0; i < 200 && restarted.Asks == 0; i++) await Task.Delay(1);
        Assert.True(restarted.Asks > 0, "the restarted server was never asked anything");
    }

    // ...but only about a file that server would actually serve. Starting Go while a Rust file is
    // on screen starts Go, and asks it nothing: it has no business being told about a .rs file.
    [Fact]
    public async Task StartingOneLanguagesServerDoesNotAskAnother()
    {
        var store = Store();
        _registry.SetActive(_first);
        var file = File_(_first, "src/main.rs");
        store.FileShown(file);
        await Hover(store, file);
        var rust = _launcher.Started[0];
        var asksBefore = rust.Asks;

        store.RetryServer(LanguageId.Of("go"));

        await Task.Delay(30);
        Assert.Equal(0, _launcher.Started[^1].Asks);
        Assert.Equal(asksBefore, rust.Asks);
    }

    private static PublishedDiagnostics Wave(string path, params string[] messages) =>
        new(
            DocumentUri.OfFile(path),
            ResultVersion.Untagged,
            messages.Select(message => new Diagnostic(
                new LspRange(
                    new LspPosition(new LspLine(0), new LspCharacter(0)),
                    new LspPosition(new LspLine(0), new LspCharacter(2))),
                DiagnosticSeverity.Error,
                message)).ToArray());

    [Fact]
    public void ShowingAFileNoServerClaimsStartsNothing()
    {
        var store = Store();
        _registry.SetActive(_first);

        store.FileShown(File_(_first, "README.md"));

        Assert.Empty(_launcher.Started);
    }

    // A server stopped by hand comes back when asked, without having to go and touch a file first.
    [Fact]
    public void AServerStoppedFromTheSettingsStartsAgainWhenAskedTo()
    {
        var store = Store();
        _registry.SetActive(_first);
        store.FileShown(File_(_first, "src/main.rs"));
        store.StopServer(LanguageId.Of("rust"));

        store.RetryServer(LanguageId.Of("rust"));

        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public async Task AHoverOnAClaimedFileStartsExactlyOneServer()
    {
        var store = Store();
        _registry.SetActive(_first);

        var hover = await Hover(store, File_(_first, "src/main.rs"));

        Assert.Single(_launcher.Started);
        Assert.Equal("`fn main()`", hover!.Markdown);
    }

    [Fact]
    public async Task ASecondHoverReusesTheServerTheFirstStarted()
    {
        var store = Store();
        _registry.SetActive(_first);

        await Hover(store, File_(_first, "src/main.rs"));
        await Hover(store, File_(_first, "src/main.rs"));

        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task AFileNoServerClaimsStartsNothing()
    {
        var store = Store();
        _registry.SetActive(_first);
        File.WriteAllText(File_(_first, "README.md"), "# hi");

        Assert.Null(await Hover(store, File_(_first, "README.md")));
        Assert.Empty(_launcher.Started);
    }

    // "Active repository only" is the memory policy the whole feature rests on: a hover in a
    // repository the user is not looking at must not start a server for it.
    [Fact]
    public async Task AHoverInARepositoryThatIsNotActiveStartsNothing()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.Null(await Hover(store, File_(_second, "src/main.rs")));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task TheActiveRepositorysServersAreTheOnesReported()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));

        Assert.NotEmpty(store.Active.Value.Servers);

        _registry.SetActive(_second);

        Assert.Empty(store.Active.Value.Servers);
        Assert.IsType<ServerState.Stopped>(store.Active.Value.StateFor(File_(_second, "src/main.rs")));
    }

    [Fact]
    public async Task ClosingARepositoryStopsItsServer()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));
        var server = _launcher.Started[0];

        _registry.RemoveRepo(_first);

        Assert.True(server.ShutdownRequests > 0);
    }

    [Fact]
    public async Task AStoppedServerIsStartedAgainByTheNextFileThatWantsIt()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));

        store.StopServer(LanguageId.Of("rust"));
        Assert.IsType<ServerState.Stopped>(store.Active.Value.StateFor(File_(_first, "src/main.rs")));

        await Hover(store, File_(_first, "src/main.rs"));

        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public void ReloadingPicksUpAConfigWrittenAfterStartup()
    {
        var store = Store(config: null);
        Assert.False(store.Handles(File_(_first, "src/main.rs")));

        File.WriteAllText(_configPath, ConfigJson);
        store.ReloadConfig();

        Assert.True(store.Handles(File_(_first, "src/main.rs")));
        Assert.True(store.Active.Value.ConfigFileExists);
    }

    [Fact]
    public void AConfigFileThatIsNotUnderstoodIsAProblemRatherThanACrash()
    {
        var store = Store(config: "{ this is not json");

        Assert.Empty(store.Active.Value.Config.Servers);
        Assert.Single(store.Active.Value.Problems);
    }

    [Fact]
    public void AStarterConfigIsWrittenForTheLanguagesTheRepositoryIsWrittenIn()
    {
        var store = Store(config: null);
        _registry.SetActive(_first);
        WaitForSuggestions(store);

        Assert.Equal(StarterConfigOutcome.Written, store.WriteStarterConfig());

        Assert.True(File.Exists(_configPath));
        Assert.Equal("rust", store.Active.Value.Config.Servers.Single().Language.Value);
    }

    // The config file is hand-written, comments and all. Offering to create one must never be a way
    // to overwrite one.
    [Fact]
    public void AConfigFileThatAlreadyExistsIsNeverRewritten()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.Equal(StarterConfigOutcome.AlreadyExists, store.WriteStarterConfig());
        Assert.Equal(ConfigJson, File.ReadAllText(_configPath));
    }

    // The repository root is listed off the UI thread, so what a suggestion depends on lands when
    // the disk answers rather than when the test asks.
    private static void WaitForSuggestions(LanguageServerStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Active.Value.Suggestions.Count > 0) return;
            Thread.Sleep(10);
        }

        Assert.Fail("no suggestion ever arrived for the repository root");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>Holds everything posted to it, so a test can prove work was handed to the UI thread
    /// rather than done on whichever thread happened to be running.</summary>
    private sealed class DeferringDispatcher : IUiDispatcher
    {
        private readonly List<Action> _queued = [];

        public void Post(Action action) => _queued.Add(action);

        public void Drain()
        {
            var due = _queued.ToArray();
            _queued.Clear();
            foreach (var action in due) action();
        }
    }

    private sealed class FakeFileSystem : IFileSystemReader
    {
        private readonly Dictionary<string, List<FileSystemEntry>> _directories = new(StringComparer.Ordinal);

        public void With(string directory, params string[] names) =>
            _directories[directory] = names.Select(n => new FileSystemEntry(n, false, false, false)).ToList();

        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
        {
            return _directories.TryGetValue(absoluteDirectory, out var entries)
                ? new DirectoryListing.Listed(entries)
                : DirectoryListing.Empty;
        }

        public string? ResolveLinkTarget(string absolutePath) => null;
    }

    /// <summary>Hands out servers that answer one hover and nothing else.</summary>
    private sealed class FakeProcessLauncher : ILanguageServerLauncher
    {
        public List<FakeSession> Started { get; } = [];

        public ServerCapabilities? Advertise { get; set; }

        /// <summary>When set, every launch fails with this reason instead of starting.</summary>
        public string? Refuse { get; set; }

        /// <summary>When set, every launched session fails its handshake with this reason.</summary>
        public string? FailHandshake { get; set; }

        public LaunchResult Launch(ServerLaunchRequest request)
        {
            if (Refuse is { } reason) return new LaunchResult.Failed(reason);

            var session = new FakeSession { HandshakeFailure = FailHandshake };
            if (Advertise is { } capabilities) session.Advertises = capabilities;
            Started.Add(session);
            return new LaunchResult.Started(session);
        }
    }

    private sealed class FakeSession : ILanguageServerSession
    {
        public event Action<ServerReadiness>? ReadinessChanged;
        public event Action<ServerExit>? Exited;

        public event Action<PublishedDiagnostics>? DiagnosticsPublished;

        public int ShutdownRequests { get; private set; }

        public int Asks { get; private set; }

        public int DefinitionAsks { get; private set; }

        public ServerCapabilities? Capabilities { get; private set; }

        public ServerCapabilities Advertises { get; set; } =
            new("fake", ServerCapabilities.Utf16, SupportsHover: true, SupportsDefinition: true, SupportsReferences: true);

        public Definition Declares { get; set; } = new Definition.None();

        public string? HandshakeFailure { get; set; }

        public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel)
        {
            if (HandshakeFailure is { } failure) return Task.FromResult<string?>(failure);
            Capabilities = Advertises;
            ReadinessChanged?.Invoke(new ServerReadiness.Handshaked());
            return Task.FromResult<string?>(null);
        }

        public Task OpenAsync(
            DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel) =>
            Task.CompletedTask;

        public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel)
        {
            Asks++;
            if (typeof(T) == typeof(Definition))
            {
                DefinitionAsks++;
                return Task.FromResult((LspResponse<T>)(object)new LspResponse<Definition>.Ok(Declares));
            }

            return Task.FromResult((LspResponse<T>)(object)new LspResponse<Hover>.Ok(
                new Hover.Text(MarkupKind.Markdown, "`fn main()`", null)));
        }

        public void RequestShutdown()
        {
            ShutdownRequests++;
            Exited?.Invoke(new ServerExit(0));
        }

        public Task CloseAsync(DocumentUri uri, CancellationToken cancel) => Task.CompletedTask;

        public void Publish(PublishedDiagnostics published) => DiagnosticsPublished?.Invoke(published);

        public void Kill() => Exited?.Invoke(new ServerExit());

        public void Dispose() { }
    }
}
