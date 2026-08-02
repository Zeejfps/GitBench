using System.Diagnostics;
using System.Text.Json;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The read tools are thin wrappers over IGitService, so they are exercised against a real fixture
// repository rather than a mock — a shape change in git's output should fail here.
public sealed class AssistantReadToolsTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _outsideSecret;
    private readonly GitService _git;
    private readonly Repo _repo;
    private readonly AssistantToolset _toolset;
    private readonly string _headSha;

    public AssistantReadToolsTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "gitbench-assistant-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "repo");
        Directory.CreateDirectory(_root);
        _outsideSecret = Path.Combine(_sandbox, "outside_secret.txt");
        File.WriteAllText(_outsideSecret, "PROD_TOKEN=hunter2\n");

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _root, "test");

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(_root, "secrets.json"), "{\"token\":\"hunter2\"}\n");
        Git("add", "a.txt", "secrets.json");
        Git("-c", "commit.gpgsign=false", "commit", "-m", "seed the tree");
        _headSha = GitOut("rev-parse", "HEAD").Trim();

        File.AppendAllText(Path.Combine(_root, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "fresh\n");
        Git("add", "b.txt");

        // Untracked and credential-shaped: the category a --no-index diff would otherwise render whole.
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", ".env"), "API_KEY=hunter2\n");

        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.GeneralAgent);
        _toolset = AssistantToolset.ForRepo(_git, _repo, agent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private void Git(params string[] args) => Run(args);

    private string GitOut(params string[] args) => Run(args);

    private string Run(string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
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

    private async Task<JsonDocument> InvokeOk(string tool, string args = "{}")
    {
        var invocation = await Invoke(tool, args);
        Assert.False(invocation.IsError, invocation.Content);
        return JsonDocument.Parse(invocation.Content);
    }

    private async Task<ToolInvocation> Invoke(string tool, string args = "{}")
    {
        var instance = _toolset.Find(tool);
        Assert.NotNull(instance);
        return await instance!.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
    }

    // The reads-only overload is what a quick action gets. Order is ordinal by name because the
    // serialized list heads the prompt-cache prefix.
    [Fact]
    public void Toolset_ExposesTheReadToolsInOrderAndNothingThatWrites()
    {
        Assert.Equal(
            new[]
            {
                "find_files", "get_branches", "get_commit_details", "get_commit_history", "get_diff",
                "get_file_at_base", "get_local_changes", "get_review_diff", "get_review_stack",
                "get_status", "read_file",
            },
            _toolset.Tools.Select(t => t.Name));
        Assert.All(_toolset.Tools, t => Assert.False(t.IsWrite));
        Assert.All(_toolset.Tools, t => JsonDocument.Parse(t.JsonSchema).Dispose());
    }

    [Fact]
    public async Task GetStatus_ReportsTheBranchAndDirtyTree()
    {
        using var json = await InvokeOk("get_status");
        Assert.Equal("main", json.RootElement.GetProperty("branch").GetString());
        Assert.False(json.RootElement.GetProperty("detached").GetBoolean());
        Assert.False(json.RootElement.GetProperty("has_upstream").GetBoolean());
        Assert.True(json.RootElement.GetProperty("dirty").GetBoolean());
    }

    [Fact]
    public async Task GetLocalChanges_SplitsStagedFromUnstaged()
    {
        using var json = await InvokeOk("get_local_changes");
        var staged = json.RootElement.GetProperty("staged");
        var unstaged = json.RootElement.GetProperty("unstaged");

        Assert.Equal("b.txt", staged[0].GetProperty("path").GetString());
        Assert.Equal("added", staged[0].GetProperty("status").GetString());
        Assert.Equal("a.txt", unstaged[0].GetProperty("path").GetString());
        Assert.Equal("modified", unstaged[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetDiff_ReturnsPrefixedUnifiedLines()
    {
        using var json = await InvokeOk("get_diff", """{"path":"a.txt","side":"unstaged"}""");
        var lines = json.RootElement.GetProperty("hunks")[0].GetProperty("lines")
            .EnumerateArray().Select(l => l.GetString()).ToList();

        Assert.Contains("+two", lines);
        Assert.Contains(" one", lines);
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetDiff_RejectsAnUnknownSide()
    {
        var invocation = await Invoke("get_diff", """{"path":"a.txt","side":"sideways"}""");
        Assert.True(invocation.IsError);
        Assert.Contains("side", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiff_RequiresACommitShaForCommitSide()
    {
        var invocation = await Invoke("get_diff", """{"path":"a.txt","side":"commit"}""");
        Assert.True(invocation.IsError);
        Assert.Contains("commit_sha", invocation.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>get_diff</c> takes a path and needs no approval, so it is guarded exactly as
    /// <c>read_file</c> is. Every entry here reaches a real file whose contents the unguarded tool
    /// returned in full, so the assertion is both the refusal and the absence of the secret.
    /// </summary>
    [Fact]
    public async Task GetDiff_RefusesEveryPathThatLeavesTheTrackedRepository()
    {
        var attempts = new (string Args, string Expect)[]
        {
            ("""{"path":"config/.env","side":"unstaged"}""", "credentials file"),
            ("""{"path":"config/.env","side":"working_tree"}""", "credentials file"),
            ($$"""{"path":"secrets.json","side":"commit","commit_sha":"{{_headSha}}"}""", "credentials file"),
            ($$"""{"path":{{JsonSerializer.Serialize(_outsideSecret)}},"side":"unstaged"}""", "outside the repository"),
            ("""{"path":"../outside_secret.txt","side":"unstaged"}""", "outside the repository"),
            ("""{"path":"a/../../outside_secret.txt","side":"working_tree"}""", "outside the repository"),
            ("""{"side":"unstaged"}""", "required"),
            ("""{"path":"a.txt","side":"commit","commit_sha":"--output=leak.patch"}""", "begin with"),
        };

        foreach (var (args, expect) in attempts)
        {
            var invocation = await Invoke("get_diff", args);
            Assert.True(invocation.IsError, $"{args} was allowed: {invocation.Content}");
            Assert.Contains(expect, invocation.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hunter2", invocation.Content, StringComparison.Ordinal);
        }
    }

    // The guard must not cost the tool its job: an ordinary tracked file still diffs on every side.
    [Fact]
    public async Task GetDiff_StillDiffsATrackedFileOnEverySide()
    {
        using var unstaged = await InvokeOk("get_diff", """{"path":"a.txt","side":"unstaged"}""");
        Assert.NotEmpty(unstaged.RootElement.GetProperty("hunks").EnumerateArray());

        using var workingTree = await InvokeOk("get_diff", """{"path":"./a.txt","side":"working_tree"}""");
        Assert.Equal("a.txt", workingTree.RootElement.GetProperty("path").GetString());

        using var staged = await InvokeOk("get_diff", """{"path":"b.txt","side":"staged"}""");
        Assert.Contains(
            "+fresh",
            staged.RootElement.GetProperty("hunks")[0].GetProperty("lines")
                .EnumerateArray().Select(l => l.GetString()));

        using var commit = await InvokeOk("get_diff", $$"""{"path":"a.txt","side":"commit","commit_sha":"{{_headSha}}"}""");
        Assert.Contains(
            "+one",
            commit.RootElement.GetProperty("hunks")[0].GetProperty("lines")
                .EnumerateArray().Select(l => l.GetString()));
    }

    // The second layer, below the tool: `--no-index` is the only diff that reads an arbitrary
    // filesystem path, so it confines the path itself rather than trusting every caller to.
    [Fact]
    public void UntrackedDiff_RefusesAPathThatIsNotRepositoryRelative()
    {
        Assert.NotNull(_git.GetDiff(_repo, _outsideSecret, DiffSide.Unstaged).ErrorMessage);
        Assert.NotNull(_git.GetDiff(_repo, "../outside_secret.txt", DiffSide.WorkingTree).ErrorMessage);
    }

    // Untracked but unremarkable: get_local_changes lists it, so the diff that explains it must work.
    [Fact]
    public async Task GetDiff_StillRendersAnUntrackedSourceFileAsAnAddition()
    {
        File.WriteAllText(Path.Combine(_root, "new.cs"), "class New;\n");

        using var json = await InvokeOk("get_diff", """{"path":"new.cs","side":"unstaged"}""");
        Assert.Contains(
            "+class New;",
            json.RootElement.GetProperty("hunks")[0].GetProperty("lines")
                .EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task GetCommitHistory_ListsTheSeedCommit()
    {
        using var json = await InvokeOk("get_commit_history", """{"limit":10}""");
        var commits = json.RootElement.GetProperty("commits");

        Assert.True(commits.GetArrayLength() >= 1);
        Assert.Equal("seed the tree", commits[0].GetProperty("summary").GetString());
        Assert.StartsWith(commits[0].GetProperty("sha").GetString()!, _headSha, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCommitDetails_CarriesTheMessageAndFileList()
    {
        using var json = await InvokeOk("get_commit_details", $$"""{"sha":"{{_headSha}}"}""");

        Assert.Contains("seed the tree", json.RootElement.GetProperty("message").GetString());
        Assert.Contains("test@test", json.RootElement.GetProperty("author").GetString());
        Assert.Equal("a.txt", json.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task GetCommitDetails_TurnsAnUnknownShaIntoAnErrorResult()
    {
        var invocation = await Invoke("get_commit_details", """{"sha":"0123456789abcdef0123456789abcdef01234567"}""");
        Assert.True(invocation.IsError);
        Assert.NotEmpty(invocation.Content);
    }

    [Fact]
    public async Task GetCommitDetails_RequiresASha()
    {
        var invocation = await Invoke("get_commit_details");
        Assert.True(invocation.IsError);
        Assert.Contains("sha", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetBranches_MarksTheCheckedOutBranch()
    {
        using var json = await InvokeOk("get_branches");
        var local = json.RootElement.GetProperty("local");

        var main = local.EnumerateArray().Single(b => b.GetProperty("name").GetString() == "main");
        Assert.True(main.GetProperty("current").GetBoolean());
        Assert.Equal("none", main.GetProperty("upstream").GetString());
        Assert.Empty(json.RootElement.GetProperty("remotes").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("stashes").EnumerateArray());
    }

    [Fact]
    public async Task ReadTools_DriveTheLoopEndToEnd()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "get_status", AssistantTestJson.Empty),
                new BackendEvent.ToolUse("call_2", "get_local_changes", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("You have one staged and one unstaged file."),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        var agent = AgentCatalog.LoadEmbedded().Get(AgentCatalog.GeneralAgent);
        var loop = new GitBench.Features.Assistant.AssistantAgentLoop(backend, agent, _toolset);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("what changed?") };

        await foreach (var _ in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None)) { }

        var results = Assert.Single(conversation.OfType<AssistantMessage.ToolResults>());
        Assert.Equal(2, results.Results.Count);
        Assert.All(results.Results, r => Assert.False(r.IsError));
        Assert.Contains("main", results.Results[0].Content, StringComparison.Ordinal);
        Assert.Contains("b.txt", results.Results[1].Content, StringComparison.Ordinal);
    }
}
