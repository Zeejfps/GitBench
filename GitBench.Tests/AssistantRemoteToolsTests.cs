using System.Diagnostics;
using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Notifications;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The fetch and pull tools driving the real <see cref="RepoOperationsStore"/> — the same path the
/// toolbar buttons take, so the spinner, the toast and the reconcile dialog behave the same whether
/// a person or the model started the operation.
/// </summary>
/// <remarks>
/// The store is real and git is scripted: the question is what the tool reports for each outcome the
/// store can produce, not what git does with a remote. The store is UI-thread-affine, so every
/// assertion here also depends on the tool reaching it by posting rather than by calling it from the
/// thread the agent loop runs on.
/// </remarks>
public sealed class AssistantRemoteToolsTests : IDisposable
{
    private readonly TempDir _root = new("gitbench-assistant-remote-");
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly ScriptedRemoteGitService _git;
    private readonly RepoOperationsStore _operations;
    private readonly AssistantToolset _toolset;
    private readonly Repo _repo;

    public AssistantRemoteToolsTests()
    {
        var statePath = Path.Combine(_root.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _repo = Open("on-screen");
        _registry.SetActive(_repo.Id);

        _git = new ScriptedRemoteGitService(new GitService(new RepoActivityTracker()));
        _operations = new RepoOperationsStore(_registry, _git, _bus, _loc, _dispatcher);
        _operations.Start();

        _toolset = AssistantToolset.Create(
            RemoteTools.CreateAll(_repo, Surface()),
            ["fetch", "pull"]);
    }

    private AssistantWriteSurface Surface() =>
        new(_dispatcher, _bus, _registry, new SilentCommitEditor(), _operations);

    public void Dispose()
    {
        _operations.Dispose();
        _registry.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void BothRemoteToolsPauseForApproval_AndEverySchemaIsValidJson()
    {
        Assert.Equal(new[] { "fetch", "pull" }, _toolset.Tools.Select(t => t.Name));
        Assert.All(_toolset.Tools, t => Assert.True(t.IsWrite, $"{t.Name} should need approval"));
        Assert.All(_toolset.Tools, t => JsonDocument.Parse(t.JsonSchema).Dispose());
    }

    [Fact]
    public void Fetch_TakesNoArguments()
    {
        var schema = JsonDocument.Parse(_toolset.Find("fetch")!.JsonSchema);

        Assert.Empty(schema.RootElement.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void Pull_OffersExactlyTheThreeStrategiesGitCanReconcileWith()
    {
        var schema = JsonDocument.Parse(_toolset.Find("pull")!.JsonSchema);

        var strategies = schema.RootElement
            .GetProperty("properties").GetProperty("strategy").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "merge", "rebase", "ff_only" }, strategies);
    }

    // ---- fetch (AC-2) ----

    [Fact]
    public void Fetch_RunsThroughTheStoreSoTheSpinnerAndToastBehaveLikeTheButton()
    {
        _git.OnFetch = _ => GitOutcome.Ok;
        var toasts = new List<ShowToastMessage>();
        _bus.Subscribe<ShowToastMessage>(toasts.Add);

        var invocation = Invoke("fetch", "{}");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal(1, _git.FetchCalls);
        Assert.False(_operations.IsBusy(_repo.Id));
        Assert.Equal(_loc.Strings.Value.ToastFetched, Assert.Single(toasts).Intent.Message);
    }

    // The store keeps its per-repo state on the UI thread with no lock; a tool that called into it
    // from the agent loop's thread would corrupt that quietly rather than loudly.
    [Fact]
    public void Fetch_IsHandedToTheUiThreadRatherThanRunFromTheCallingOne()
    {
        _git.OnFetch = _ => GitOutcome.Ok;
        var tool = _toolset.Find("fetch")!;

        var task = tool.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);

        Assert.Equal(0, _git.FetchCalls);
        Assert.True(_dispatcher.Queued > 0, "the fetch should be posted and still pending");

        Pump.WaitFor(_dispatcher, () => task.IsCompleted, "the fetch tool to finish");

        Assert.Equal(1, _git.FetchCalls);
    }

    // The spinner is the person's only sign that the model started something long-running.
    [Fact]
    public void Fetch_RaisesTheSpinnerWhileItIsStillOutstanding()
    {
        using var held = _git.HoldRemoteCalls();
        _git.OnFetch = _ => GitOutcome.Ok;
        var tool = _toolset.Find("fetch")!;

        var task = tool.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => _operations.IsBusy(_repo.Id), "the spinner to come up");

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void Fetch_WhenGitFails_ReportsWhatGitSaid()
    {
        _git.OnFetch = _ => GitOutcome.Fail("could not read from remote repository");

        var invocation = Invoke("fetch", "{}");

        Assert.True(invocation.IsError);
        Assert.Contains("could not read from remote repository", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Fetch_WhenGitThrows_ReportsTheExceptionRatherThanFailingTheTurn()
    {
        _git.OnFetch = _ => throw new InvalidOperationException("git exploded");

        var invocation = Invoke("fetch", "{}");

        Assert.True(invocation.IsError);
        Assert.Contains("git exploded", invocation.Content, StringComparison.Ordinal);
    }

    // Nothing was fetched. An ok result carrying a status field would be one field away from the
    // model telling the person their repository is up to date.
    [Fact]
    public void Fetch_WhileAFetchIsAlreadyRunning_ComesBackAsAnErrorThatCannotReadAsFetched()
    {
        using var held = _git.HoldRemoteCalls();
        _git.OnFetch = _ => GitOutcome.Ok;
        var first = _toolset.Find("fetch")!.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => _operations.IsBusy(_repo.Id), "the first fetch to start");

        var second = Invoke("fetch", "{}");

        Assert.True(second.IsError);
        Assert.Equal(1, _git.FetchCalls);
        Assert.False(first.IsCompleted);
    }

    // ---- pull (AC-3) ----

    [Theory]
    [InlineData("merge", PullStrategy.Merge)]
    [InlineData("rebase", PullStrategy.Rebase)]
    [InlineData("ff_only", PullStrategy.FastForwardOnly)]
    public void Pull_ForwardsTheStrategyItWasNamed(string name, PullStrategy expected)
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        var invocation = Invoke("pull", $$"""{"strategy":"{{name}}"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal(expected, Assert.Single(_git.PullStrategies));
    }

    [Fact]
    public void Pull_WithoutAStrategy_LeavesTheChoiceToGitsConfiguredDefault()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        Invoke("pull", "{}");

        Assert.Null(Assert.Single(_git.PullStrategies));
    }

    [Fact]
    public void Pull_WithAStrategyItDoesNotOffer_IsRefusedByNameWithoutPulling()
    {
        _git.OnPull = (_, _) => PullOutcome.Ok;

        var invocation = Invoke("pull", """{"strategy":"squash"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("squash", invocation.Content, StringComparison.Ordinal);
        Assert.Equal(0, _git.PullCalls);
    }

    // A diverged pull moved nothing, so it cannot come back as a completed pull — and the message
    // has to hand the model the choice git refused to make, or its only next move is to try again
    // with the same call.
    [Fact]
    public void Pull_WhenTheBranchDiverges_ComesBackAsAnErrorNamingTheStrategiesItCanRetryWith()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();

        var invocation = Invoke("pull", "{}");

        Assert.True(invocation.IsError);
        Assert.Contains("merge", invocation.Content, StringComparison.Ordinal);
        Assert.Contains("rebase", invocation.Content, StringComparison.Ordinal);
    }

    // Both things are true at once: the model is told to pick a strategy and the person is shown the
    // reconcile dialog that asks them to pick one. Pinned so the collision is a decision rather than
    // a surprise.
    [Fact]
    public void Pull_WhenTheBranchDiverges_AlsoPutsTheReconcileDialogInFrontOfThePerson()
    {
        _git.OnPull = (_, _) => new PullOutcome.Diverged();
        var diverged = new List<PullDivergedMessage>();
        _bus.Subscribe<PullDivergedMessage>(diverged.Add);

        Invoke("pull", "{}");

        Assert.Equal(_repo, Assert.Single(diverged).Repo);
    }

    [Fact]
    public void Pull_WhenGitFails_ReportsWhatGitSaid()
    {
        _git.OnPull = (_, _) => PullOutcome.Fail("refusing to merge unrelated histories");

        var invocation = Invoke("pull", "{}");

        Assert.True(invocation.IsError);
        Assert.Contains("unrelated histories", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_WhileAPullIsAlreadyRunning_ComesBackAsAnErrorThatCannotReadAsPulled()
    {
        using var held = _git.HoldRemoteCalls();
        _git.OnPull = (_, _) => PullOutcome.Ok;
        var first = _toolset.Find("pull")!.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => _operations.IsBusy(_repo.Id), "the first pull to start");

        var second = Invoke("pull", "{}");

        Assert.True(second.IsError);
        Assert.Equal(1, _git.PullCalls);
        Assert.False(first.IsCompleted);
    }

    // Same-type only: the store lets a pull run alongside a fetch, and so must the tools.
    [Fact]
    public async Task Pull_WhileAFetchIsRunning_StillRuns()
    {
        using var held = _git.HoldRemoteCalls();
        _git.OnFetch = _ => GitOutcome.Ok;
        _git.OnPull = (_, _) => PullOutcome.Ok;
        _ = _toolset.Find("fetch")!.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => _git.FetchCalls == 1, "the fetch to reach git");

        var pull = _toolset.Find("pull")!.InvokeAsync(AssistantTestJson.Empty, CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => _git.PullCalls == 1, "the pull to reach git");
        held.Dispose();
        Pump.WaitFor(_dispatcher, () => pull.IsCompleted, "the pull tool to finish");

        Assert.False((await pull).IsError);
    }

    private ToolInvocation Invoke(string tool, string args)
    {
        var instance = _toolset.Find(tool);
        Assert.NotNull(instance);
        var task = instance!.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, $"the {tool} tool to finish");
        return task.GetAwaiter().GetResult();
    }

    private Repo Open(string name)
    {
        var path = Path.Combine(_root.Path, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.email", "test@test");
        Git(path, "config", "user.name", "test");
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(path));
        return _registry.Repos.Single(r => r.Path == path);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }
}
