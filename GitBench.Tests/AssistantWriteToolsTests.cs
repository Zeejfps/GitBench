using System.Diagnostics;
using System.Text.Json;
using GitBench.App;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Branches;
using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Platform;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The write tools against a real repository and the real commit-bar view model. set_commit_message
// is asserted through that view model on purpose: it has to go through the box the user is looking
// at, not around it.
public sealed class AssistantWriteToolsTests : IDisposable
{
    private readonly string _root;
    private readonly string _other;
    private readonly GitService _git;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalChangesViewModel _commitBox;
    private readonly AssistantToolset _toolset;
    private readonly Repo _repo;
    private readonly Repo _inactive;
    private string? _remote;

    public AssistantWriteToolsTests()
    {
        _root = NewDir("gitbench-assistant-write-");
        _other = NewDir("gitbench-assistant-other-");
        _git = new GitService(new NullActivityTracker());

        InitRepo(_root);
        InitRepo(_other);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
        Git(_root, "add", "a.txt");
        Git(_root, "-c", "commit.gpgsign=false", "commit", "-m", "seed the tree");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "fresh\n");
        File.AppendAllText(Path.Combine(_root, "a.txt"), "two\n");

        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_root));
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(_other));
        _repo = _registry.Repos.Single(r => r.Path == _root);
        _inactive = _registry.Repos.Single(r => r.Path == _other);
        _registry.SetActive(_repo.Id);

        _commitBox = new LocalChangesViewModel(
            _registry,
            _git,
            _dispatcher,
            new FrameTicker(),
            _bus,
            new LocalChangesSelectionStore(),
            new NoopShell(),
            new NoopClipboard(),
            new PreferencesService(Preferences.Default, Path.Combine(_root, "prefs.json")),
            new IdleSnapshotStore(),
            new LocalizationService(new State<Locale>(Locale.En)));

        _toolset = ToolsetFor(_repo);
    }

    private AssistantToolset ToolsetFor(Repo repo) =>
        AssistantToolset.ForRepo(
            _git,
            repo,
            AgentCatalog.LoadEmbedded().Get(AgentCatalog.GeneralAgent),
            new ReviewProgressStore(),
            new AssistantWriteSurface(_dispatcher, _bus, _registry, _commitBox, new IdleRemoteOperations()));

    public void Dispose()
    {
        _commitBox.Dispose();
        Delete(_root);
        Delete(_other);
        if (_remote is not null) Delete(_remote);
    }

    [Fact]
    public void ToolsetCarriesTheWritesAndEveryOneOfThemNeedsApproval()
    {
        Assert.Equal(
            new[]
            {
                "commit", "create_tag", "fetch", "find_files", "get_branches", "get_commit_details",
                "get_commit_history", "get_conflict", "get_conflicts", "get_diff",
                "get_file_at_base", "get_local_changes", "get_review_diff", "get_review_stack",
                "get_status", "mark_viewed", "pull", "push_tag", "read_file", "resolve_conflict",
                "set_commit_message", "stage_files", "unstage_files",
            },
            _toolset.Tools.Select(t => t.Name));

        var writes = _toolset.Tools.Where(t => t.IsWrite).Select(t => t.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "commit", "create_tag", "fetch", "mark_viewed", "pull", "push_tag",
                "resolve_conflict", "set_commit_message", "stage_files", "unstage_files",
            },
            writes);
        Assert.All(_toolset.Tools, t => JsonDocument.Parse(t.JsonSchema).Dispose());
    }

    [Fact]
    public void StageFiles_MovesThePathsIntoTheIndexAndTellsTheApp()
    {
        var told = 0;
        _bus.Subscribe<WorkingTreeChangedMessage>(_ => told++);

        var invocation = Invoke("stage_files", """{"paths":["b.txt"]}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("b.txt", Staged());
        Assert.Equal(1, told);
    }

    [Fact]
    public void UnstageFiles_TakesThePathBackOut()
    {
        Git(_root, "add", "b.txt");
        Assert.Contains("b.txt", Staged());

        var invocation = Invoke("unstage_files", """{"paths":["b.txt"]}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.DoesNotContain("b.txt", Staged());
    }

    [Fact]
    public void StageFiles_WithoutPaths_IsAnErrorResultRatherThanAnException()
    {
        var invocation = Invoke("stage_files", """{"paths":[]}""");

        Assert.True(invocation.IsError);
        Assert.Contains("paths", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StageFiles_OnAPathGitRejects_ReportsWhatGitSaid()
    {
        var invocation = Invoke("stage_files", """{"paths":["nowhere/at/all.txt"]}""");

        Assert.True(invocation.IsError);
        // This text is the model's only account of why the call failed, so it has to carry git's
        // own words — the rejected path and the reason — not a summary that drops them.
        Assert.Contains("nowhere/at/all.txt", invocation.Content, StringComparison.Ordinal);
        Assert.Contains("did not match", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    // The point of the phase: the text lands in the commit bar's own state, so its bindings update
    // and the user watches it arrive.
    [Fact]
    public void SetCommitMessage_LandsInTheCommitBoxsViewModel()
    {
        var invocation = Invoke(
            "set_commit_message",
            """{"title":"Fix the parser","description":"It choked on empty hunks."}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("Fix the parser", _commitBox.Title.Value);
        Assert.Equal("It choked on empty hunks.", _commitBox.Description.Value);
    }

    [Fact]
    public void SetCommitMessage_WithoutADescription_ClearsTheBody()
    {
        Invoke("set_commit_message", """{"title":"First","description":"a body"}""");

        Invoke("set_commit_message", """{"title":"Second"}""");

        Assert.Equal("Second", _commitBox.Title.Value);
        Assert.Equal(string.Empty, _commitBox.Description.Value);
    }

    // The commit box on screen belongs to the active repo; a session bound to another checkout must
    // not type into it.
    [Fact]
    public void SetCommitMessage_FromARepoThatIsNotOnScreen_IsRefused()
    {
        var elsewhere = ToolsetFor(_inactive);

        var invocation = Invoke("""{"title":"nope"}""", elsewhere.Find("set_commit_message")!);

        Assert.True(invocation.IsError);
        Assert.Equal(string.Empty, _commitBox.Title.Value);
    }

    // The repo check runs off the UI thread, before the write is posted. There is one commit box for
    // the whole app, so if the user switches repos in that gap the write has to be dropped rather
    // than land in front of a different checkout.
    [Fact]
    public async Task SetCommitMessage_WhenTheRepoOnScreenChangesBeforeTheWriteLands_TypesNothing()
    {
        _dispatcher.Drain();
        var tool = _toolset.Find("set_commit_message")!;

        var task = tool.InvokeAsync(
            AssistantTestJson.Element("""{"title":"Fix the parser","description":"It choked."}"""),
            CancellationToken.None);
        // The tool passed its own repo check and posted the write, but the post has not been drained
        // yet — this is exactly the gap the guard has to survive.
        Assert.True(_dispatcher.Queued > 0, "the write should be posted and still pending");
        Assert.Equal(string.Empty, _commitBox.Title.Value);

        _registry.SetActive(_inactive.Id);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, "the set_commit_message tool to finish");
        await task;

        Assert.Equal(string.Empty, _commitBox.Title.Value);
        Assert.Equal(string.Empty, _commitBox.Description.Value);
    }

    [Fact]
    public void Commit_CreatesTheCommitAndEmptiesTheBoxItCommitted()
    {
        Git(_root, "add", "b.txt");
        Invoke("set_commit_message", """{"title":"Add b","description":"the body"}""");

        var invocation = Invoke("commit", """{"message":"Add b\n\nthe body"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("Add b", GitOut(_root, "log", "-1", "--format=%s").Trim());
        Assert.Equal(string.Empty, _commitBox.Title.Value);
        Assert.Equal(string.Empty, _commitBox.Description.Value);
    }

    // A draft that is not what was committed is the person's own writing for a later commit.
    [Fact]
    public void Commit_LeavesADraftThatIsNotWhatWasCommittedAlone()
    {
        Git(_root, "add", "b.txt");
        Invoke("set_commit_message", """{"title":"Something else entirely"}""");

        Invoke("commit", """{"message":"Add b"}""");

        Assert.Equal("Something else entirely", _commitBox.Title.Value);
    }

    [Fact]
    public void Commit_WithNothingStaged_ReportsTheFailure()
    {
        var invocation = Invoke("commit", """{"message":"empty"}""");

        Assert.True(invocation.IsError);
    }

    [Fact]
    public void CreateTag_NamesHeadAndTellsTheApp()
    {
        var told = 0;
        _bus.Subscribe<RefsChangedMessage>(_ => told++);

        var invocation = Invoke("create_tag", """{"name":"v1.0.0"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("v1.0.0", Tags());
        Assert.Equal(1, told);
        // A lightweight tag is the ref itself pointing at the commit; nothing else was created.
        Assert.Equal("commit", GitOut(_root, "cat-file", "-t", "v1.0.0").Trim());
    }

    [Fact]
    public void CreateTag_WithAMessage_IsAnnotated()
    {
        var invocation = Invoke("create_tag", """{"name":"v1.0.0","message":"the first one"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("tag", GitOut(_root, "cat-file", "-t", "v1.0.0").Trim());
        Assert.Contains("the first one", GitOut(_root, "tag", "-n", "-l", "v1.0.0"));
    }

    [Fact]
    public void CreateTag_OnTheCommitYouName_LeavesHeadAlone()
    {
        var first = GitOut(_root, "rev-parse", "HEAD").Trim();
        Git(_root, "add", "b.txt");
        Git(_root, "-c", "commit.gpgsign=false", "commit", "-m", "second");

        var invocation = Invoke("create_tag", $$"""{"name":"v0.9.0","commit_sha":"{{first}}"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("v0.9.0", GitOut(_root, "tag", "--points-at", first));
        Assert.Empty(GitOut(_root, "tag", "--points-at", "HEAD").Trim());
    }

    [Fact]
    public void CreateTag_WithANameGitWouldNotTake_IsRefusedBeforeAnythingIsWritten()
    {
        var invocation = Invoke("create_tag", """{"name":"v 1.0"}""");

        Assert.True(invocation.IsError);
        Assert.Empty(Tags());
    }

    [Fact]
    public void CreateTag_OnACommitThatDoesNotResolve_IsAnError()
    {
        var invocation = Invoke("create_tag", """{"name":"v1.0.0","commit_sha":"deadbee"}""");

        Assert.True(invocation.IsError);
        Assert.Empty(Tags());
    }

    [Fact]
    public void CreateTag_WithPush_PutsTheTagOnTheRemote()
    {
        AddOrigin();

        var invocation = Invoke("create_tag", """{"name":"v1.0.0","message":"ship it","push":true}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("refs/tags/v1.0.0", GitOut(_root, "ls-remote", "--tags", "origin"));
        Assert.Contains("origin", invocation.Content, StringComparison.Ordinal);
    }

    // Asked to publish with nowhere to publish to. Creating the tag anyway would report a failure
    // and leave the tag behind, which is the state nobody asked for.
    [Fact]
    public void CreateTag_WithPush_AndNoRemote_LeavesNoTagBehind()
    {
        var invocation = Invoke("create_tag", """{"name":"v1.0.0","push":true}""");

        Assert.True(invocation.IsError);
        Assert.Empty(Tags());
    }

    // The failure the assistant actually walked into: it tagged without pushing, was then asked to
    // push, and had nothing but create_tag to reach for. The error has to hand it the tool that can.
    [Fact]
    public void CreateTag_OnANameThatIsAlreadyTaken_PointsAtPushTag()
    {
        Invoke("create_tag", """{"name":"v1.0.0"}""");

        var invocation = Invoke("create_tag", """{"name":"v1.0.0","push":true}""");

        Assert.True(invocation.IsError);
        Assert.Contains("push_tag", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void PushTag_PublishesATagThatWasCreatedWithoutPushing()
    {
        AddOrigin();
        Invoke("create_tag", """{"name":"v1.0.0"}""");
        Assert.Empty(GitOut(_root, "ls-remote", "--tags", "origin").Trim());

        var invocation = Invoke("push_tag", """{"name":"v1.0.0","remote":"origin"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("refs/tags/v1.0.0", GitOut(_root, "ls-remote", "--tags", "origin"));
    }

    [Fact]
    public void PushTag_WithoutARemote_ReachesEveryRemoteTheRepositoryHas()
    {
        AddOrigin();
        Invoke("create_tag", """{"name":"v1.0.0","message":"ship it"}""");

        var invocation = Invoke("push_tag", """{"name":"v1.0.0"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Contains("refs/tags/v1.0.0", GitOut(_root, "ls-remote", "--tags", "origin"));
        Assert.Contains("origin", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void PushTag_OnATagThatDoesNotExist_SaysSoRatherThanFailingAtTheRemote()
    {
        AddOrigin();

        var invocation = Invoke("push_tag", """{"name":"v9.9.9"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("v9.9.9", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void PushTag_ToARemoteTheRepositoryDoesNotHave_NamesTheOnesItDoes()
    {
        AddOrigin();
        Invoke("create_tag", """{"name":"v1.0.0"}""");

        var invocation = Invoke("push_tag", """{"name":"v1.0.0","remote":"upstream"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("origin", invocation.Content, StringComparison.Ordinal);
        Assert.Empty(GitOut(_root, "ls-remote", "--tags", "origin").Trim());
    }

    private void AddOrigin()
    {
        _remote = NewDir("gitbench-assistant-remote-");
        Git(_remote, "init", "--bare", "--initial-branch=main");
        Git(_root, "remote", "add", "origin", _remote);
    }

    private string Tags() => GitOut(_root, "tag", "--list");

    private IReadOnlyList<string> Staged()
    {
        var changes = Assert.IsType<Fetched<LocalChangesSnapshot>.Ok>(_git.GetLocalChanges(_repo));
        return changes.Value.Staged.Select(f => f.Path).ToArray();
    }

    private ToolInvocation Invoke(string tool, string args)
    {
        var instance = _toolset.Find(tool);
        Assert.NotNull(instance);
        return Invoke(args, instance!);
    }

    // A write tool hops to the UI thread to touch view models and the bus, so the dispatcher has to
    // be pumped before it can finish.
    private ToolInvocation Invoke(string args, IAssistantTool tool)
    {
        var task = tool.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, $"the {tool.Name} tool to finish");
        return task.GetAwaiter().GetResult();
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private static void InitRepo(string path)
    {
        Git(path, "init", "--initial-branch=main");
        Git(path, "config", "user.email", "test@test");
        Git(path, "config", "user.name", "test");
    }

    private static void Git(string workingDirectory, params string[] args) => Run(workingDirectory, args);

    private static string GitOut(string workingDirectory, params string[] args) => Run(workingDirectory, args);

    private static string Run(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private sealed class NoopShell : IPlatformShell
    {
        public void OpenFolder(string path) { }
        public void OpenTerminal(string path) { }
        public void OpenFile(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class NoopClipboard : IClipboard
    {
        public void SetText(string text) { }
        public string? GetText() => null;
    }

    // The commit box's file lists come from the snapshot store; nothing here exercises them.
    private sealed class IdleSnapshotStore : IRepoSnapshotStore
    {
        public IReadable<Fetched<CommitSnapshot>?> Commits { get; } = new State<Fetched<CommitSnapshot>?>(null);
        public IReadable<Fetched<BranchListing>?> Branches { get; } = new State<Fetched<BranchListing>?>(null);
        public IReadable<Fetched<LocalChangesData>?> LocalChanges { get; } = new State<Fetched<LocalChangesData>?>(null);
    }
}
