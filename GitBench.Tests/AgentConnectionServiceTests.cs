using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using McpSdk.Adapter.System.Text.Json;
using McpSdk.Server;
using ZGF.Gui.Desktop;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The agent-connections service against a recording server host: the preference starts, stops
/// and restarts the server, generates its token once and persists it, reports a port it cannot
/// have, and the settings view model refuses a port that is not one.
/// </summary>
public sealed class AgentConnectionServiceTests : IDisposable
{
    private sealed class RecordingHost : IMcpServerHost
    {
        public List<McpServerOptions> Starts { get; } = new();
        public int Stops { get; private set; }
        public Func<McpServerOptions, McpServerStart> Answer { get; set; } =
            options => new McpServerStart.Started(new Uri($"http://127.0.0.1:{options.Port}/mcp/{options.PathToken?.Value}"));

        public McpServerStart Start(McpServerOptions options)
        {
            Starts.Add(options);
            return Answer(options);
        }

        public void Stop() => Stops++;
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly TempDir _dir = new("gitbench-agent-connections-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly PreferencesService _preferences;
    private readonly State<AgentConnectionSettings> _settings;
    private readonly State<AgentConnectionState> _state = new(new AgentConnectionState.Off());
    private readonly RecordingHost _host = new();
    private readonly AgentToolMcpSource _source;

    public AgentConnectionServiceTests()
    {
        _preferences = new PreferencesService(Preferences.Default, Path.Combine(_dir.Path, "prefs.json"));
        _settings = new State<AgentConnectionSettings>(AgentConnectionSettings.From(_preferences.Current));
        _settings.Changed += s => _preferences.Update(p => p.WithAgentConnections(s.Enabled, s.Port, s.Token));

        var statePath = Path.Combine(_dir.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        var git = new GitService(new NullActivityTracker());
        var surface = new AssistantWriteSurface(
            _dispatcher, new MessageBus(), registry, new SilentCommitEditor(), new IdleRemoteOperations(), new TestDocuments.Empty());
        var windows = new NoReviewWindows();
        _source = new AgentToolMcpSource(
            new AgentToolExport(git, new UnparsedFiles(), new ReviewProgressStore(), windows, surface, new NoPairingSessions()),
            registry, windows, surface, TimeProvider.System);
    }

    public void Dispose()
    {
        _preferences.Dispose();
        _dir.Dispose();
    }

    private AgentConnectionService Service() =>
        new(_settings, _state, _source, new WalkthroughPrompt(), _host);

    [Fact]
    public void Disabled_StartsNothing_AndReportsOff()
    {
        using var service = Service();

        Assert.Empty(_host.Starts);
        Assert.IsType<AgentConnectionState.Off>(_state.Value);
    }

    [Fact]
    public void Enabling_GeneratesAToken_StartsTheServer_AndPersistsBoth()
    {
        using var service = Service();

        _settings.Value = _settings.Value with { Enabled = true };

        var options = Assert.Single(_host.Starts);
        Assert.NotNull(options.PathToken);
        Assert.Equal(AgentConnectionSettings.DefaultPort, options.Port);
        Assert.Equal(AgentConnectionService.ServerName, options.ServerName);
        Assert.False(options.IncludeGuiTools);
        Assert.Same(_source, Assert.Single(options.ToolSources));
        Assert.Equal(AgentConnectionInstructions.Text, options.Instructions);
        var listening = Assert.IsType<AgentConnectionState.Listening>(_state.Value);
        Assert.EndsWith($"/mcp/{options.PathToken!.Value}", listening.Endpoint.ToString());
        Assert.Equal($"claude mcp add --transport http diffdino {listening.Endpoint}", listening.ClaudeMcpAddCommand);

        Assert.True(_preferences.Current.AgentConnectionsEnabled);
        Assert.Equal(options.PathToken, _preferences.Current.AgentConnectionsToken);
    }

    [Fact]
    public void TheToken_IsGeneratedOnce()
    {
        using var service = Service();
        _settings.Value = _settings.Value with { Enabled = true };
        var token = _settings.Value.Token;

        _settings.Value = _settings.Value with { Enabled = false };
        _settings.Value = _settings.Value with { Enabled = true };

        Assert.Equal(token, _settings.Value.Token);
        Assert.All(_host.Starts, o => Assert.Equal(token, o.PathToken));
    }

    [Fact]
    public void Disabling_StopsTheServer()
    {
        using var service = Service();
        _settings.Value = _settings.Value with { Enabled = true };

        _settings.Value = _settings.Value with { Enabled = false };

        Assert.Equal(1, _host.Stops);
        Assert.IsType<AgentConnectionState.Off>(_state.Value);
        Assert.False(_preferences.Current.AgentConnectionsEnabled);
    }

    [Fact]
    public void ChangingThePort_RestartsOnTheNewOne()
    {
        using var service = Service();
        _settings.Value = _settings.Value with { Enabled = true };

        _settings.Value = _settings.Value with { Port = 6001 };

        Assert.Equal(1, _host.Stops);
        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(6001, _host.Starts[1].Port);
        Assert.Equal(6001, _preferences.Current.AgentConnectionsPort);
        Assert.Contains(":6001/", Assert.IsType<AgentConnectionState.Listening>(_state.Value).Endpoint.ToString());
    }

    [Fact]
    public void ChangingThePortWhileOff_StartsNothing()
    {
        using var service = Service();

        _settings.Value = _settings.Value with { Port = 6002 };

        Assert.Empty(_host.Starts);
        Assert.Equal(6002, _preferences.Current.AgentConnectionsPort);
    }

    [Fact]
    public void ABusyPort_MovesUpToTheNextFreeOne_AndKeepsTheChosenPort()
    {
        _host.Answer = options => options.Port < AgentConnectionSettings.DefaultPort + 2
            ? new McpServerStart.Failed("port in use")
            : new McpServerStart.Started(new Uri($"http://127.0.0.1:{options.Port}/mcp/{options.PathToken?.Value}"));
        using var service = Service();

        _settings.Value = _settings.Value with { Enabled = true };

        Assert.Equal(3, _host.Starts.Count);
        var listening = Assert.IsType<AgentConnectionState.Listening>(_state.Value);
        Assert.Contains($":{AgentConnectionSettings.DefaultPort + 2}/", listening.Endpoint.ToString());
        Assert.Equal(AgentConnectionSettings.DefaultPort, _preferences.Current.AgentConnectionsPort);

        _settings.Value = _settings.Value with { Enabled = false };
        Assert.Equal(1, _host.Stops);
    }

    [Fact]
    public void EveryPortBusy_ReportsTheFailure_AndDisablingStopsNothing()
    {
        _host.Answer = _ => new McpServerStart.Failed("port in use");
        using var service = Service();

        _settings.Value = _settings.Value with { Enabled = true };
        Assert.Equal(AgentConnectionService.PortAttempts, _host.Starts.Count);
        Assert.Equal("port in use", Assert.IsType<AgentConnectionState.Failed>(_state.Value).Reason);

        _settings.Value = _settings.Value with { Enabled = false };
        Assert.Equal(0, _host.Stops);
        Assert.IsType<AgentConnectionState.Off>(_state.Value);
    }

    [Fact]
    public void TheDebugServerAlreadyUp_ReportsItsEndpoint_AndIsLeftAlone()
    {
        var debug = new Uri("http://127.0.0.1:5577/mcp");
        _host.Answer = _ => new McpServerStart.AlreadyRunning(debug);
        using var service = Service();

        _settings.Value = _settings.Value with { Enabled = true };
        var failed = Assert.IsType<AgentConnectionState.Failed>(_state.Value);
        Assert.Contains(debug.ToString(), failed.Reason);

        _settings.Value = _settings.Value with { Enabled = false };
        Assert.Equal(0, _host.Stops);
    }

    [Fact]
    public void SessionCount_RidesOnTheListeningState()
    {
        using var service = Service();
        _settings.Value = _settings.Value with { Enabled = true };
        Assert.Equal(0, Assert.IsType<AgentConnectionState.Listening>(_state.Value).Sessions);

        // The source counts on the UI thread, which is this test's dispatcher.
        using var ended = new CancellationTokenSource();
        _source.Register(new McpSession("s1", ended.Token), new DefaultToolsController(new SystemJson()));
        _dispatcher.Drain();
        Assert.Equal(1, Assert.IsType<AgentConnectionState.Listening>(_state.Value).Sessions);

        ended.Cancel();
        _dispatcher.Drain();
        Assert.Equal(0, Assert.IsType<AgentConnectionState.Listening>(_state.Value).Sessions);
    }

    [Fact]
    public void Preferences_RoundTripTheToken_AndDropAnInvalidOne()
    {
        var path = Path.Combine(_dir.Path, "roundtrip.json");
        var token = McpPathToken.Generate();
        PreferencesStore.Save(path, Preferences.Default with
        {
            AgentConnectionsEnabled = true,
            AgentConnectionsPort = 6100,
            AgentConnectionsToken = token,
        });

        var loaded = PreferencesStore.Load(path);
        Assert.True(loaded.AgentConnectionsEnabled);
        Assert.Equal(6100, loaded.AgentConnectionsPort);
        Assert.Equal(token, loaded.AgentConnectionsToken);

        File.WriteAllText(path, """{"agentConnectionsEnabled":true,"agentConnectionsPort":70000,"agentConnectionsToken":"has/slash"}""");
        var repaired = PreferencesStore.Load(path);
        Assert.True(repaired.AgentConnectionsEnabled);
        Assert.Equal(AgentConnectionSettings.DefaultPort, repaired.AgentConnectionsPort);
        Assert.Null(repaired.AgentConnectionsToken);
    }

    [Fact]
    public void PreferencesService_RefusesAPortOutsideTheRange()
    {
        _preferences.Update(p => p.WithAgentConnections(true, 0, null));
        Assert.False(_preferences.Current.AgentConnectionsEnabled);

        _preferences.Update(p => p.WithAgentConnections(true, 65536, null));
        Assert.False(_preferences.Current.AgentConnectionsEnabled);
    }

    [Fact]
    public void SettingsViewModel_SavesAValidPort_AndRefusesAnInvalidOne()
    {
        using var service = Service();
        using var vm = new AgentConnectionsSettingsViewModel(
            _settings, _state, new NoopClipboard(), new MessageBus(), new LocalizationService(new State<Locale>(Locale.En)));

        vm.PortDraft.Value = "abc";
        Assert.True(vm.PortInvalid.Value);
        Assert.Equal(AgentConnectionSettings.DefaultPort, _settings.Value.Port);

        vm.PortDraft.Value = "70000";
        Assert.True(vm.PortInvalid.Value);
        Assert.Equal(AgentConnectionSettings.DefaultPort, _settings.Value.Port);

        vm.PortDraft.Value = "6200";
        Assert.False(vm.PortInvalid.Value);
        Assert.Equal(6200, _settings.Value.Port);
        Assert.Equal(6200, _preferences.Current.AgentConnectionsPort);

        vm.Enabled.Value = true;
        Assert.True(_settings.Value.Enabled);
        Assert.Equal("6200", vm.PortDraft.Value);
        Assert.Contains(":6200/", Assert.IsType<AgentConnectionState.Listening>(_state.Value).Endpoint.ToString());
        Assert.True(vm.CanCopy.Value);
        Assert.Contains("Listening on http://127.0.0.1:6200/", vm.StatusText.Value);
    }

    private sealed class NoopClipboard : ZGF.Gui.IClipboard
    {
        public void SetText(string text) { }
        public string? GetText() => null;
    }
}
