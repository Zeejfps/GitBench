using GitBench.Features.Diff;
using GitBench.Features.LanguageServers;
using GitBench.Features.Notifications;
using GitBench.Localization;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The settings card's rules: what it offers, what it refuses to do to a file the user wrote by
/// hand, and what each action says afterwards.
/// </summary>
public sealed class LanguageServersViewModelTests : IDisposable
{
    private readonly FakeStore _store = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly MessageBus _bus = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly List<ToastIntent> _toasts = [];

    private static readonly StarterServer Rust = StarterServers.All.Single(s => s.Language.Value == "rust");

    private LanguageServersViewModel Model()
    {
        _bus.SubscribeScoped<ShowToastMessage>(message => _toasts.Add(message.Intent));
        return new LanguageServersViewModel(_store, _loc, new ImmediateDispatcher(), _bus, _clipboard);
    }

    public void Dispose() => _loc.Dispose();

    private static LanguageServerConfig Parsed(string json) =>
        Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(json)).Config;

    // A failed server raises the operation-error dialog wherever it was started from, including
    // from this dialog's own Start button. Reporting it here as well showed the reader the same
    // message twice, one stacked on the other.
    [Fact]
    public void StartingAServerReportsNothingItself()
    {
        var errors = new List<ShowOperationErrorMessage>();
        _bus.SubscribeScoped<ShowOperationErrorMessage>(errors.Add);
        _store.Snapshot.Value = LanguageServerSnapshot.Nothing with
        {
            Config = Parsed("""{"servers":{"rust":{"command":"rust-analyzer","extensions":[".rs"]}}}"""),
            Servers =
            [
                new ServerStatus(
                    new RepositoryId(Guid.NewGuid()), LanguageId.Of("rust"), new ServerState.Failed("boom")),
            ],
        };
        var vm = Model();

        vm.Restart(vm.Servers.Value.Single());

        Assert.Equal([LanguageId.Of("rust")], _store.Restarted);
        Assert.Empty(errors);
    }

    [Fact]
    public void WithNoConfigFileAndSomethingToOfferTheStarterConfigIsOffered()
    {
        _store.Snapshot.Value = LanguageServerSnapshot.Nothing with { Suggestions = [Rust] };

        Assert.True(Model().CanCreateConfig.Value);
    }

    [Fact]
    public void WithAConfigFileAlreadyThereTheStarterConfigIsNotOffered()
    {
        _store.Snapshot.Value = LanguageServerSnapshot.Nothing with
        {
            Suggestions = [Rust],
            ConfigFileExists = true,
        };

        Assert.False(Model().CanCreateConfig.Value);
    }

    [Fact]
    public void WithNothingToOfferTheStarterConfigIsNotOffered() =>
        Assert.False(Model().CanCreateConfig.Value);

    [Fact]
    public void WritingAStarterConfigSaysSoWhenItWorked()
    {
        _store.WriteOutcome = StarterConfigOutcome.Written;

        Model().CreateConfig();

        Assert.Single(_toasts);
    }

    [Fact]
    public void WritingAStarterConfigOverAnExistingOneSaysItWasLeftAlone()
    {
        _store.WriteOutcome = StarterConfigOutcome.AlreadyExists;

        Model().CreateConfig();

        Assert.Equal("A config file already exists; it was left alone.", Assert.Single(_toasts).Message);
    }

    // Nothing happened, so nothing is announced — a toast for a no-op is a toast the reader learns
    // to ignore.
    [Fact]
    public void AStarterConfigThatCouldNotBeWrittenSaysNothing()
    {
        _store.WriteOutcome = StarterConfigOutcome.NotWritten;

        Model().CreateConfig();

        Assert.Empty(_toasts);
    }

    [Fact]
    public void CopyingAnEntryPutsSomethingAConfigFileWouldAccept()
    {
        Model().CopyEntry(Rust);

        var merged = Parsed($$"""{ "servers": { {{_clipboard.Text}} } }""");
        Assert.Equal("rust-analyzer", merged.ServerFor(LanguageId.Of("rust"))!.Command);
        Assert.Single(_toasts);
    }

    [Fact]
    public void StoppingAndStartingActOnTheServersOwnLanguage()
    {
        var configured = new ConfiguredServer(
            Parsed("""{ "servers": { "go": { "command": "gopls", "extensions": [".go"] } } }""").Servers[0],
            new ServerState.Ready());
        var vm = Model();

        vm.Stop(configured);
        vm.Restart(configured);

        Assert.Equal(["go"], _store.Stopped.Select(l => l.Value));
        Assert.Equal(["go"], _store.Restarted.Select(l => l.Value));
    }

    [Fact]
    public void ReloadingAsksTheStoreToReadTheFileAgain()
    {
        Model().Reload();

        Assert.Equal(1, _store.Reloads);
    }

    [Fact]
    public void AFailedServerIsDescribedWithoutTheReasonItFailed()
    {
        var description = Model().Describe(new ServerState.Failed("'rust-analyzer' was not found."));

        Assert.DoesNotContain("'rust-analyzer' was not found.", description, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void EveryStateHasWordsOfItsOwn()
    {
        var vm = Model();
        ServerState[] states =
        [
            new ServerState.NotConfigured(),
            new ServerState.Stopped(),
            new ServerState.Starting(),
            new ServerState.Indexing(null),
            new ServerState.Indexing(42),
            new ServerState.Ready(),
            new ServerState.Failed("broken"),
        ];

        var described = states.Select(vm.Describe).ToArray();

        Assert.All(described, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(described.Length, described.Distinct().Count());
    }

    private sealed class FakeStore : ILanguageServerStore
    {
        public State<LanguageServerSnapshot> Snapshot { get; } = new(LanguageServerSnapshot.Nothing);

        public List<LanguageId> Stopped { get; } = [];

        public List<LanguageId> Restarted { get; } = [];

        public int Reloads { get; private set; }

        public StarterConfigOutcome WriteOutcome { get; set; } = StarterConfigOutcome.Written;

        public IReadable<LanguageServerSnapshot> Active => Snapshot;

        public State<FileDiagnostics> Reported { get; } = new(FileDiagnostics.None);

        public IReadable<FileDiagnostics> Diagnostics => Reported;

        public string ConfigPath => "/somewhere/language-servers.json";

        public void FileShown(string absolutePath) { }

        public bool CanDefine(string absolutePath) => false;

        public Task<DefinitionReply> DefineAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct) =>
            Task.FromResult(DefinitionReply.Nothing);

        public bool CanReference(string absolutePath) => false;

        public Task<ReferenceReply> ReferencesAsync(
            string absolutePath, FileLine line, RawColumn column, CancellationToken ct) =>
            Task.FromResult<ReferenceReply>(ReferenceReply.Unavailable.Instance);

        public void ReloadConfig() => Reloads++;

        public void RetryServer(LanguageId language) => Restarted.Add(language);

        public void StopServer(LanguageId language) => Stopped.Add(language);

        public StarterConfigOutcome WriteStarterConfig() => WriteOutcome;

        public bool Handles(string absolutePath) => false;

        public Task<GitBench.Lsp.Documents.HoverText?> HoverAsync(
            string repoRoot,
            string absolutePath,
            GitBench.Features.Diff.FileLine line,
            GitBench.Features.Diff.RawColumn column,
            CancellationToken cancel) =>
            Task.FromResult<GitBench.Lsp.Documents.HoverText?>(null);
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;

        public string? GetText() => Text;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
