using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GitBench.App;
using GitBench.Features.AgentConnections;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Pairing;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Automation;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The assistant's review tools over a real MCP server on a free port: the tool list matches the
/// assistant's own definitions with <c>repo</c> added, a call answers what the assistant tool
/// answers, the repository argument resolves by name and by path, a bare endpoint is gated by the
/// token, and a session that ends — or stops calling back — releases the walkthrough it narrated.
/// </summary>
// In the CodeIntel collection because get_review_diff reads DiffOptions.StructureEnabled, which
// DiffHunkHeaderTests flips: xUnit runs collections in parallel, so sharing one serializes them.
[Collection(nameof(CodeIntelCollection))]
public sealed class AgentToolMcpSourceTests : IDisposable
{
    private sealed class EmptyStackSource : IReviewStackSource
    {
        public Task<ReviewStack> LoadAsync(ReviewSession session, int cap) =>
            Task.FromResult(new ReviewStack(session.RepoId, "base", "head", "base", session.HeadLabel, [], false));
    }

    private sealed class IdleSnapshotStore : IRepoSnapshotStore
    {
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges { get; } = new State<Fetched<LocalChangesData>?>(null);
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private readonly TempDir _dir = new("gitbench-agent-mcp-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly GitService _git = new(new NullActivityTracker());
    private readonly RepoRegistry _registry;
    private readonly Repo _repo;
    private readonly ReviewWindowsViewModel _windows;
    private readonly FixedPairingSessions _pairing = new();
    private readonly AgentToolExport _export;
    private readonly AgentToolMcpSource _source;
    private readonly McpPathToken _token = McpPathToken.Generate();
    private readonly GuiMcpServer _server;
    private readonly McpTestClient _client;

    public AgentToolMcpSourceTests()
    {
        Git("init", "-q", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "one\ntwo\nthree\n");
        Git("add", ".");
        Git("-c", "commit.gpgsign=false", "commit", "-q", "-m", "seed the tree");
        Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_dir.Path, "a.txt"), "one\nTWO\nthree\n");
        Git("add", ".");
        Git("-c", "commit.gpgsign=false", "commit", "-q", "-m", "rewrite the second line");

        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        if (_registry.Open(_dir.Path) != OpenRepoOutcome.Opened)
            throw new InvalidOperationException("The fixture repository did not open.");
        _repo = _registry.Repos.Single();
        _registry.SetActive(_repo.Id);

        var loc = new LocalizationService(new State<Locale>(Locale.En));
        _windows = TestWindows.Review(
            _bus,
            new EmptyStackSource(),
            _registry,
            _git,
            new UnparsedFiles(),
            new IdleSnapshotStore(),
            new ReviewProgressStore(),
            _dispatcher,
            loc,
            new PreferencesService(Preferences.Default, Path.Combine(_dir.Path, "prefs.json")));

        var surface = new AssistantWriteSurface(
            _dispatcher, _bus, _registry, new SilentCommitEditor(), new IdleRemoteOperations(), new TestDocuments.Empty());
        _export = new AgentToolExport(_git, new UnparsedFiles(), new ReviewProgressStore(), _windows, surface, _pairing);
        _source = new AgentToolMcpSource(_export, _registry, _windows, surface, _clock);

        _server = new GuiMcpServer(
            new McpServerOptions
            {
                Port = McpTestClient.FreePort(),
                ServerName = AgentConnectionService.ServerName,
                Instructions = AgentConnectionInstructions.Text,
                PathToken = _token,
                ToolSources = [_source],
                Prompts = new WalkthroughPrompt(),
                IncludeGuiTools = false,
            },
            new GuiDriver(() => [], new InlineDispatcher(), (_, _, _) => { }));
        _client = new McpTestClient(_server.Endpoint);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _windows.Dispose();
        _dispatcher.Drain();
        _dir.Dispose();
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    // A call hops to the UI thread, which is this test's dispatcher: pump it until the answer lands.
    private T Await<T>(Task<T> task, string what)
    {
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);
        return task.GetAwaiter().GetResult();
    }

    private void Await(Task task, string what)
    {
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);
        task.GetAwaiter().GetResult();
    }

    private ReviewWindowViewModel OpenWindow()
    {
        _bus.Broadcast(new OpenReviewWindowMessage(_repo.Id, "feature", "feature"));
        return _windows.Windows[^1];
    }

    private static object OneStep(string repo) =>
        new { repo, steps = new[] { new { title = "The entry point", body_md = "It starts here." } } };

    [Fact]
    public async Task ToolsList_IsTheAssistantsDefinitions_WithRepoAdded()
    {
        var init = await _client.Initialize();
        Assert.Equal(AgentConnectionService.ServerName, init.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(AgentConnectionInstructions.Text, init.GetProperty("instructions").GetString());

        var listed = await _client.ListTools();
        var definitions = _export.Definitions();

        Assert.Equal(
            definitions.Select(d => d.Tool.Name).OrderBy(n => n, StringComparer.Ordinal),
            listed.Select(t => t.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var definition in definitions)
        {
            var tool = listed.Single(t => t.GetProperty("name").GetString() == definition.Tool.Name);
            Assert.Equal(definition.Tool.Description, tool.GetProperty("description").GetString());
            Assert.Equal(!definition.Tool.IsWrite, tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

            using var literal = JsonDocument.Parse(definition.Tool.JsonSchema);
            var schema = tool.GetProperty("inputSchema");
            var properties = schema.GetProperty("properties");
            Assert.True(properties.TryGetProperty(AgentToolSchema.RepoArgument, out _), $"{definition.Tool.Name} lacks repo");
            if (literal.RootElement.TryGetProperty("properties", out var expected))
                foreach (var property in expected.EnumerateObject())
                    Assert.True(
                        JsonElement.DeepEquals(property.Value, properties.GetProperty(property.Name)),
                        $"{definition.Tool.Name}.{property.Name} was not exported verbatim");

            var required = literal.RootElement.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];
            var listedRequired = schema.TryGetProperty("required", out var listedRequiredElement)
                ? listedRequiredElement.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];
            Assert.Equal(required, listedRequired);
        }
    }

    [Fact]
    public async Task GetReviewStack_AnswersWhatTheAssistantToolAnswers()
    {
        await _client.Initialize();
        var direct = await ReviewTools.CreateReads(_git, _repo, new UnparsedFiles())
            .Single(t => t.Name == "get_review_stack")
            .InvokeAsync(AssistantTestJson.Element("{}"), CancellationToken.None);

        var result = Await(_client.Call("get_review_stack", new { repo = _dir.Path }), "the call");

        Assert.False(McpTestClient.IsError(result));
        Assert.Equal(direct.Content, McpTestClient.TextOf(result));
    }

    [Fact]
    public async Task RepoByName_AndByPathInsideIt_BothResolve()
    {
        await _client.Initialize();

        var byName = Await(_client.Call("get_review_stack", new { repo = _repo.DisplayName }), "the call by name");
        var byPath = Await(_client.Call("get_review_stack", new { repo = Path.Combine(_dir.Path, "sub", "dir") }), "the call by path");

        Assert.False(McpTestClient.IsError(byName));
        Assert.Equal(McpTestClient.TextOf(byName), McpTestClient.TextOf(byPath));
    }

    [Fact]
    public async Task NoRepoArgument_FallsBackToTheActiveRepo()
    {
        await _client.Initialize();

        var result = Await(_client.Call("get_review_stack", new { }), "the call");

        Assert.False(McpTestClient.IsError(result));
        Assert.Contains("feature", McpTestClient.TextOf(result));
    }

    [Fact]
    public async Task UnknownRepo_IsAnErrorNamingTheOpenOnes()
    {
        await _client.Initialize();

        var result = Await(_client.Call("get_review_stack", new { repo = "nope" }), "the call");

        Assert.True(McpTestClient.IsError(result));
        var text = McpTestClient.TextOf(result);
        Assert.Contains("'nope'", text);
        Assert.Contains(_repo.DisplayName, text);
        Assert.Contains(_dir.Path, text);
    }

    [Fact]
    public async Task ToolError_MapsToIsError()
    {
        await _client.Initialize();

        var result = Await(_client.Call("get_review_diff", new { repo = _dir.Path, path = "" }), "the call");

        Assert.True(McpTestClient.IsError(result));
        Assert.Contains("'path' is required", McpTestClient.TextOf(result));
    }

    [Fact]
    public async Task BareEndpoint_Is404_WithTheTokenSet()
    {
        using var bare = new McpTestClient(new Uri(_server.Endpoint, "/mcp"));

        Assert.Equal(HttpStatusCode.NotFound, await bare.ProbeStatus());
    }

    [Fact]
    public async Task WalkthroughPrompt_CarriesTheFocus()
    {
        await _client.Initialize();

        var prompt = await _client.GetPrompt(WalkthroughPrompt.Name, new { focus = "the parser" });

        var message = prompt.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        var text = message.GetProperty("content").GetProperty("text").GetString();
        Assert.Contains("walkthrough_step", text);
        Assert.Contains("Concentrate on: the parser", text);
    }

    [Fact]
    public async Task SessionDelete_ReleasesTheBlockedStep_AndMarksTheStoreDisconnected()
    {
        await _client.Initialize();
        Pump.WaitFor(_dispatcher, () => _source.OpenSessions.Value == 1, "the session to be counted");
        var window = OpenWindow();

        // The blocked call's response is the ended session's to drop, so it is not awaited: what
        // matters is that the store it was waiting on is released and told why.
        var call = _client.Call("walkthrough_step", OneStep(_dir.Path));
        _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Phase.Value is WalkthroughPhase.Narrating { Waiting: true }, "the step to block");
        Assert.False(call.IsCompleted);

        Await(_client.Delete(), "the delete");

        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Phase.Value is WalkthroughPhase.Disconnected, "the store to be disconnected");
        Pump.WaitFor(_dispatcher, () => _source.OpenSessions.Value == 0, "the session to be uncounted");
        Assert.Equal(0, _clock.LiveTimers);
    }

    [Fact]
    public async Task NoWalkthroughCallWithinThePresenceTimeout_MarksTheStoreDisconnected()
    {
        await _client.Initialize();
        var window = OpenWindow();

        var call = _client.Call("walkthrough_step", OneStep(_dir.Path));
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Phase.Value is WalkthroughPhase.Narrating { Waiting: true }, "the step to block");
        window.Walkthrough.Next();
        var result = Await(call, "the step to return");
        using (var json = JsonDocument.Parse(McpTestClient.TextOf(result)))
            Assert.Equal("next", json.RootElement.GetProperty("action").GetString());

        _dispatcher.Drain();
        Assert.Equal(1, _clock.LiveTimers);
        Assert.IsType<WalkthroughPhase.Narrating>(window.Walkthrough.Phase.Value);

        // A further walkthrough call is the agent's presence: the watch is dropped while it runs.
        var again = _client.Call("walkthrough_step", OneStep(_dir.Path));
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Phase.Value is WalkthroughPhase.Narrating { Waiting: true }, "the second step to block");
        Assert.Equal(0, _clock.LiveTimers);
        window.Walkthrough.Next();
        Await(again, "the second step to return");
        _dispatcher.Drain();
        Assert.Equal(1, _clock.LiveTimers);

        _clock.Advance(AgentSession.PresenceTimeout);
        _dispatcher.Drain();

        Assert.IsType<WalkthroughPhase.Disconnected>(window.Walkthrough.Phase.Value);
        Assert.Equal(0, _clock.LiveTimers);
    }

    [Fact]
    public async Task WalkthroughStep_WithoutAWindow_IsAnErrorNamingReviewOpen()
    {
        await _client.Initialize();

        var result = Await(_client.Call("walkthrough_step", OneStep(_dir.Path)), "the call");

        Assert.True(McpTestClient.IsError(result));
        Assert.Contains("review_open", McpTestClient.TextOf(result));
        _dispatcher.Drain();
        Assert.Equal(0, _clock.LiveTimers);
    }

    [Fact]
    public async Task RepoArgumentThatIsNotAString_IsRefused()
    {
        await _client.Initialize();

        var result = Await(_client.Call("get_review_stack", new { repo = 7 }), "the call");

        Assert.True(McpTestClient.IsError(result));
        Assert.Contains("repo", McpTestClient.TextOf(result));
    }
    private PairingStore StartPairing()
    {
        var store = new PairingStore("Add a retry", "Test agent", new RecordingPairingPresentation(), new ScriptedWorkspace(), _dispatcher, _clock);
        store.MarkRunning();
        _pairing.Stores[_repo.Id] = store;
        return store;
    }

    [Fact]
    public async Task PairingLoop_RoadmapStopWaitDone_OverMcp()
    {
        await _client.Initialize();
        var store = StartPairing();

        var roadmap = Await(_client.Call("pairing_roadmap", new { repo = _dir.Path, milestones = new[] { new { title = "Retry" } } }), "the roadmap");
        Assert.False(McpTestClient.IsError(roadmap));
        Assert.Single(store.Roadmap.Value);

        var stop = Await(_client.Call("pairing_stop", new
        {
            repo = _dir.Path, path = "src/Client.cs", symbol = "Fetch", title = "Retry the fetch", reason = "It fails transiently.",
            code = "public void Fetch() => Retry(Send);",
        }), "the stop");
        Assert.False(McpTestClient.IsError(stop));
        using (var json = JsonDocument.Parse(McpTestClient.TextOf(stop)))
            Assert.Equal(1, json.RootElement.GetProperty("stop").GetInt32());

        var wait = _client.Call("pairing_wait", new { repo = _dir.Path });
        Pump.WaitFor(_dispatcher, () => store.Phase.Value is PairingPhase.Running { Waiting: true }, "the wait to attach");
        Await(store.DoneAsync(), "Done");
        var done = Await(wait, "the wait to return");

        using var result = JsonDocument.Parse(McpTestClient.TextOf(done));
        Assert.Equal("done", result.RootElement.GetProperty("action").GetString());
        Assert.True(result.RootElement.TryGetProperty("diff", out _));
        store.Dispose();
    }

    [Fact]
    public async Task PairingTool_WithoutASession_SaysHowToStartOne()
    {
        await _client.Initialize();

        var result = Await(_client.Call("pairing_state", new { repo = _dir.Path }), "the call");

        Assert.True(McpTestClient.IsError(result));
        Assert.Contains("New pairing session", McpTestClient.TextOf(result));
    }
}
