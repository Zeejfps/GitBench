using System.Text.Json;
using GitBench.App;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Review.Walkthrough;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The walkthrough tools over a real review-window registry: a batch reaches the window's store,
/// the blocking narrator's call returns what the reviewer did, the built-in assistant's returns at
/// once, bad input is refused field by field, and no window names the way back.
/// </summary>
public sealed class WalkthroughToolsTests : IDisposable
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

    private readonly TempDir _dir = new("gitbench-walkthrough-tools-");
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly Repo _repo;
    private readonly ReviewWindowsViewModel _windows;

    public WalkthroughToolsTests()
    {
        _repo = new Repo(Guid.NewGuid(), _dir.Path, "repo");
        var git = new GitService(new NullActivityTracker());
        var statePath = Path.Combine(_dir.Path, "repos.json");
        var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        var loc = new LocalizationService(new State<Locale>(Locale.En));
        _windows = TestWindows.Review(
            _bus,
            new EmptyStackSource(),
            registry,
            git,
            new UnparsedFiles(),
            new IdleSnapshotStore(),
            new ReviewProgressStore(),
            _dispatcher,
            loc,
            new PreferencesService(Preferences.Default, Path.Combine(_dir.Path, "prefs.json")));
    }

    public void Dispose()
    {
        _windows.Dispose();
        _dir.Dispose();
    }

    private ReviewWindowViewModel OpenWindow(string head = "feature")
    {
        _bus.Broadcast(new OpenReviewWindowMessage(_repo.Id, head, head));
        return _windows.Windows[^1];
    }

    private AssistantToolset Tools(WalkthroughNarratorMode mode) =>
        AssistantToolset.Create(
            WalkthroughTools.CreateAll(_repo, _windows, _dispatcher, mode),
            ["walkthrough_step", "walkthrough_wait", "walkthrough_end"]);

    private Task<ToolInvocation> Start(AssistantToolset tools, string name, string args = "{}")
    {
        var tool = tools.Find(name);
        Assert.NotNull(tool);
        return tool!.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
    }

    private ToolInvocation Invoke(AssistantToolset tools, string name, string args = "{}")
    {
        var task = Start(tools, name, args);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, $"the {name} tool to finish");
        return task.GetAwaiter().GetResult();
    }

    private const string OneStep =
        """
        {"steps":[{"title":"The entry point","body_md":"It starts **here**.","focus":{"path":"a.txt","line":3},"spotlights":[{"path":"a.txt","from":3,"to":5,"note":"the call"}]}]}
        """;

    [Fact]
    public void ImmediateStep_ShowsTheBatch_AndReturnsShownAtOne()
    {
        var window = OpenWindow();

        var result = Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_step", OneStep);

        Assert.False(result.IsError, result.Content);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal("shown", json.RootElement.GetProperty("action").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("at").GetInt32());
        var card = Assert.IsType<WalkthroughCard.Step>(window.Walkthrough.Card.Value);
        Assert.Equal("The entry point", card.Content.Title);
        Assert.Equal(new FileLine(3), card.Content.Focus!.Value.Line);
        Assert.Equal("the call", Assert.Single(card.Content.Spotlights).Note);
        Assert.Equal(NarratorKind.Assistant, Assert.IsType<WalkthroughPhase.Narrating>(window.Walkthrough.Phase.Value).Who.Kind);
    }

    [Fact]
    public void ImmediateWait_IsAnError()
    {
        OpenWindow();

        var result = Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_wait");

        Assert.True(result.IsError);
        Assert.Contains("next message", result.Content);
    }

    [Fact]
    public void BlockingStep_ReturnsWhenTheReviewerStepsPastTheBatch()
    {
        var window = OpenWindow();
        var tools = Tools(WalkthroughNarratorMode.Blocking);

        var call = Start(tools, "walkthrough_step", """{"steps":[{"title":"one","body_md":"a"},{"title":"two","body_md":"b"}]}""");
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Current.Value == 0, "the batch to show");
        Assert.False(call.IsCompleted);
        Assert.Equal(NarratorKind.McpSession, Assert.IsType<WalkthroughPhase.Narrating>(window.Walkthrough.Phase.Value).Who.Kind);

        window.Walkthrough.Next();
        Pump.DrainFor(_dispatcher, TimeSpan.FromMilliseconds(50));
        Assert.False(call.IsCompleted);

        window.Walkthrough.Next();
        Pump.WaitFor(_dispatcher, () => call.IsCompleted, "the step call to return");

        using var json = JsonDocument.Parse(call.Result.Content);
        Assert.Equal("next", json.RootElement.GetProperty("action").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("at").GetInt32());
    }

    [Fact]
    public void BlockingWait_ReturnsTheQuestionWithTheSelection()
    {
        var window = OpenWindow();
        var tools = Tools(WalkthroughNarratorMode.Blocking);
        var shown = Start(tools, "walkthrough_step", OneStep);
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Current.Value == 0, "the batch to show");

        window.Walkthrough.Ask("why here?");
        Pump.WaitFor(_dispatcher, () => shown.IsCompleted, "the step call to return");
        using var json = JsonDocument.Parse(shown.Result.Content);
        Assert.Equal("ask", json.RootElement.GetProperty("action").GetString());
        Assert.Equal("why here?", json.RootElement.GetProperty("question").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("at").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("selection", out _));

        var wait = Start(tools, "walkthrough_wait");
        Pump.WaitFor(_dispatcher, () => window.Walkthrough.Phase.Value is WalkthroughPhase.Narrating { Waiting: true }, "the wait to attach");
        window.Walkthrough.Next();
        Pump.WaitFor(_dispatcher, () => wait.IsCompleted, "the wait call to return");
        using var next = JsonDocument.Parse(wait.Result.Content);
        Assert.Equal("next", next.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void BlockingWait_WithNothingShowing_IsAnError()
    {
        OpenWindow();

        var result = Invoke(Tools(WalkthroughNarratorMode.Blocking), "walkthrough_wait");

        Assert.True(result.IsError);
        Assert.Contains("walkthrough_step", result.Content);
    }

    [Fact]
    public void End_ClearsTheWalkthrough_AndLeavesTheSummary()
    {
        var window = OpenWindow();
        var tools = Tools(WalkthroughNarratorMode.Immediate);
        Invoke(tools, "walkthrough_step", OneStep);

        var result = Invoke(tools, "walkthrough_end", """{"summary_md":"All done."}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("""{"ok":true}""", result.Content);
        Assert.Equal("All done.", Assert.IsType<WalkthroughCard.Finished>(window.Walkthrough.Card.Value).SummaryMarkdown);
        Assert.Empty(window.Walkthrough.Steps.Value);
    }

    [Fact]
    public void NoWindow_NamesReviewOpen()
    {
        var result = Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_step", OneStep);

        Assert.True(result.IsError);
        Assert.Contains("review_open", result.Content);
    }

    [Fact]
    public void TheMostRecentlyOpenedWindowForTheRepo_IsTheTarget()
    {
        var older = OpenWindow("one");
        var newer = OpenWindow("two");

        Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_step", OneStep);

        Assert.False(older.Walkthrough.IsVisible.Value);
        Assert.True(newer.Walkthrough.IsVisible.Value);
    }

    [Theory]
    [InlineData("{}", "steps")]
    [InlineData("""{"steps":[]}""", "at least one")]
    [InlineData("""{"steps":[{"body_md":"x"}]}""", "steps[0].title")]
    [InlineData("""{"steps":[{"title":"t"}]}""", "steps[0].body_md")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x","focus":{"path":"a","line":0}}]}""", "steps[0].focus.line")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x","focus":{"path":"a","line":"3"}}]}""", "steps[0].focus.line")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x","focus":{"path":"a","line":3,"side":"left"}}]}""", "steps[0].focus.side")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x","spotlights":[{"path":"a","from":5,"to":3}]}]}""", "steps[0].spotlights[0].to")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x"},{"title":"u","body_md":"y","spotlights":[{"from":1,"to":2}]}]}""", "steps[1].spotlights[0].path")]
    [InlineData("""{"steps":[{"title":"t","body_md":"x","dim":"yes"}]}""", "steps[0].dim")]
    public void BadInput_IsRefusedByField(string args, string expected)
    {
        OpenWindow();

        var result = Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_step", args);

        Assert.True(result.IsError, result.Content);
        Assert.Contains(expected, result.Content);
    }

    [Fact]
    public void SideDefaultsToNew_AndOldIsHonoured()
    {
        var window = OpenWindow();

        Invoke(Tools(WalkthroughNarratorMode.Immediate), "walkthrough_step",
            """{"steps":[{"title":"t","body_md":"x","focus":{"path":"a","line":3,"side":"old"},"spotlights":[{"path":"a","from":1,"to":1}],"dim":true}]}""");

        var step = Assert.IsType<WalkthroughCard.Step>(window.Walkthrough.Card.Value).Content;
        Assert.Equal(DiffLineSide.Old, step.Focus!.Value.Side);
        Assert.Equal(DiffLineSide.New, Assert.Single(step.Spotlights).Side);
        Assert.True(step.Dim);
    }

    [Fact]
    public void ToolsAreReads()
    {
        Assert.All(Tools(WalkthroughNarratorMode.Blocking).Tools, tool => Assert.False(tool.IsWrite));
    }

    [Fact]
    public void ASelection_IsWrittenInFull()
    {
        var quote = new DiffSelectionQuote("a.txt", new FileLine(3), new FileLine(5), DiffQuoteSide.Removed, "x = 1", "Foo.Bar");

        var json = WalkthroughTools.WriteAction(new WalkthroughAction.Ask(0, "q", quote));

        using var doc = JsonDocument.Parse(json);
        var selection = doc.RootElement.GetProperty("selection");
        Assert.Equal("a.txt", selection.GetProperty("path").GetString());
        Assert.Equal("removed", selection.GetProperty("side").GetString());
        Assert.Equal(3, selection.GetProperty("start_line").GetInt32());
        Assert.Equal(5, selection.GetProperty("end_line").GetInt32());
        Assert.Equal("x = 1", selection.GetProperty("text").GetString());
        Assert.Equal("Foo.Bar", selection.GetProperty("declaration").GetString());
    }
}
