using System.Diagnostics;
using System.Text.Json;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.LocalChanges;
using GitBench.Features.Repos;
using GitBench.Features.Review;
using GitBench.Git;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The review tools against a real branch: a base resolved the way the review window resolves it, a
/// file list, per-file diffs at that base, and the one write — a Viewed mark that goes through the
/// same store a reviewer's checkbox writes to, and expires the same way.
/// </summary>
// In the CodeIntel collection because get_review_diff reads DiffOptions.StructureEnabled, which
// DiffHunkHeaderTests flips: xUnit runs collections in parallel, so sharing one serializes them.
[Collection(nameof(CodeIntelCollection))]
public sealed class AssistantReviewToolsTests : IDisposable
{
    private sealed class NullActivityTracker : IRepoActivityTracker
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public IDisposable Begin(string repoPath) => new Scope();
        public bool IsActive(string repoPath) => false;
    }

    // The commit box plays no part in a Viewed mark; the write surface is here for the thread it
    // hops to, not for what else it reaches.
    private sealed class NoopCommitEditor : ICommitEditor
    {
        public IReadable<string> Title { get; } = new State<string>(string.Empty);
        public IReadable<string> Description { get; } = new State<string>(string.Empty);
        public void SetTitle(string value) { }
        public void SetDescription(string value) { }
    }

    private const int BigLineLength = 1000;
    private const int BigLineCount = 200;

    private readonly string _root;
    private readonly GitService _git;
    private readonly Repo _repo;
    private readonly ReviewProgressStore _progress = new();
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly AssistantToolset _toolset;

    public AssistantReviewToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-review-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(new NullActivityTracker());

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@test");
        Git("config", "user.name", "test");
        Write("a.txt", "one\ntwo\nthree\n");
        Write("b.txt", "kept\n");
        // Committed at the base and left alone by the branch: a secret the repository should never
        // have tracked, and a file long enough to be worth capping.
        Write(".env", "API_TOKEN=sk-live-not-a-real-secret\n");
        Write("big.txt", string.Concat(Enumerable.Repeat(new string('x', BigLineLength) + "\n", BigLineCount)));
        Git("add", ".");
        Commit("seed the tree");

        // A branch off main with two commits, which is what a review is: main is the base the
        // auto-resolver lands on, since the branch has no upstream.
        Git("checkout", "-b", "feature");
        Write("a.txt", "one\nTWO\nthree\n");
        Git("add", ".");
        Commit("rewrite the second line");
        Write("c.txt", "added on the branch\n");
        Git("add", ".");
        Commit("add c");

        _repo = new Repo(Guid.NewGuid(), _root, "test") { Branch = "feature" };
        _toolset = ReviewToolsetFor(_repo);
    }

    private AssistantToolset ReviewToolsetFor(Repo repo) =>
        AssistantToolset.Create(
            [
                .. ReviewTools.CreateReads(_git, repo, new UnparsedFiles()),
                .. ReviewTools.CreateWrites(_git, repo, _progress, WriteSurface()),
            ],
            ["get_file_at_base", "get_review_diff", "get_review_stack", "mark_viewed"]);

    private AssistantWriteSurface WriteSurface()
    {
        var statePath = Path.Combine(_root, ".git", "repos.json");
        return new AssistantWriteSurface(
            _dispatcher,
            new MessageBus(),
            new RepoRegistry(RepoStateStore.Load(statePath), statePath),
            new NoopCommitEditor(),
            new IdleRemoteOperations());
    }

    public void Dispose()
    {
        try { TempDir.ForceDelete(new DirectoryInfo(_root)); } catch { /* best effort */ }
    }

    private void Write(string path, string text) => File.WriteAllText(Path.Combine(_root, path), text);

    private void Commit(string message) => Git("-c", "commit.gpgsign=false", "commit", "-m", message);

    private string Git(params string[] args)
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

    // mark_viewed hops to the UI thread to touch the reviewer's own store, so the dispatcher has to
    // be pumped before it can finish.
    private ToolInvocation Invoke(string tool, string args = "{}")
    {
        var instance = _toolset.Find(tool);
        Assert.NotNull(instance);
        var task = instance!.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, $"the {tool} tool to finish");
        return task.GetAwaiter().GetResult();
    }

    private JsonDocument InvokeOk(string tool, string args = "{}")
    {
        var invocation = Invoke(tool, args);
        Assert.False(invocation.IsError, invocation.Content);
        return JsonDocument.Parse(invocation.Content);
    }

    [Fact]
    public void ReviewReadsAreSilentAndTheMarkIsNot()
    {
        Assert.False(_toolset.Find("get_review_stack")!.IsWrite);
        Assert.False(_toolset.Find("get_review_diff")!.IsWrite);
        Assert.False(_toolset.Find("get_file_at_base")!.IsWrite);
        Assert.True(_toolset.Find("mark_viewed")!.IsWrite);
    }

    // The chat agent is where these are actually offered, and mark_viewed must have arrived as a
    // write rather than slipping in beside the reads.
    [Fact]
    public void TheChatAgent_OffersTheReviewToolsWithTheMarkAsItsOnlyReviewWrite()
    {
        var allowed = AgentCatalog.LoadEmbedded().Get(AgentCatalog.GeneralAgent).AllowedTools;

        Assert.Contains("get_review_stack", allowed);
        Assert.Contains("get_review_diff", allowed);
        Assert.Contains("get_file_at_base", allowed);
        Assert.Contains("mark_viewed", allowed);

        Assert.Equal(
            new[] { "mark_viewed" },
            ReviewTools.CreateWrites(_git, _repo, _progress, WriteSurface()).Select(t => t.Name));
        Assert.All(ReviewTools.CreateReads(_git, _repo, new UnparsedFiles()), t => Assert.False(t.IsWrite));
    }

    [Fact]
    public void GetReviewStack_ReportsTheBaseTheCommitsAndTheFiles()
    {
        using var json = InvokeOk("get_review_stack");
        var root = json.RootElement;

        Assert.Equal("feature", root.GetProperty("head_ref").GetString());
        Assert.Equal("main", root.GetProperty("base_ref").GetString());
        Assert.Equal(
            new[] { "add c", "rewrite the second line" },
            root.GetProperty("commits").EnumerateArray()
                .Select(c => c.GetProperty("summary").GetString()).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "a.txt", "c.txt" },
            root.GetProperty("files").EnumerateArray()
                .Select(f => f.GetProperty("path").GetString()).OrderBy(s => s, StringComparer.Ordinal));
    }

    // What is under review is the checkout, not the Repo record the toolset was handed. The record is
    // immutable and the registry publishes a branch switch by replacing it, so a session built before
    // the switch holds the old branch for good — here the record still says "feature".
    [Fact]
    public void GetReviewStack_FollowsABranchSwitchMadeAfterTheToolsetWasBuilt()
    {
        Git("checkout", "-b", "second", "main");
        Write("d.txt", "added on second\n");
        Git("add", ".");
        Commit("add d");

        using var json = InvokeOk("get_review_stack");
        var root = json.RootElement;

        Assert.Equal("feature", _repo.Branch);
        Assert.Equal("second", root.GetProperty("head_ref").GetString());
        Assert.Equal("main", root.GetProperty("base_ref").GetString());
        Assert.Equal(
            new[] { "add d" },
            root.GetProperty("commits").EnumerateArray().Select(c => c.GetProperty("summary").GetString()));
        Assert.Equal(
            new[] { "d.txt" },
            root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
    }

    // The review's diff is base→tip, not the last commit: a.txt changed in the first commit only, so
    // a HEAD-relative diff would come back empty here.
    [Fact]
    public void GetReviewDiff_SpansTheWholeRangeRatherThanTheLastCommit()
    {
        using var json = InvokeOk("get_review_diff", """{"path":"a.txt"}""");
        var lines = json.RootElement.GetProperty("hunks")[0].GetProperty("lines")
            .EnumerateArray().Select(l => l.GetString()).ToList();

        Assert.Contains("+TWO", lines);
        Assert.Contains("-two", lines);
    }

    [Fact]
    public void GetFileAtBase_ReturnsThePreChangeContent()
    {
        using var json = InvokeOk("get_file_at_base", """{"path":"a.txt"}""");

        Assert.Equal("one\ntwo\nthree\n", json.RootElement.GetProperty("content").GetString());
        Assert.Equal("main", json.RootElement.GetProperty("base_ref").GetString());
    }

    // Reading at a ref is still reading this repository's files, so the base side is behind the same
    // guard read_file is: a name that is a secret is refused however it is addressed.
    [Fact]
    public void GetFileAtBase_RefusesACredentialShapedNameEvenThoughTheBaseTracksIt()
    {
        Assert.Contains("sk-live-not-a-real-secret", Git("show", "main:.env"), StringComparison.Ordinal);

        var invocation = Invoke("get_file_at_base", """{"path":".env"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("credentials file", invocation.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", invocation.Content, StringComparison.Ordinal);
    }

    // The file worth reading at the base is often the one the branch deleted, and it is not on disk
    // to open — so the guard is asked the diff question rather than the open-a-file one.
    [Fact]
    public void GetFileAtBase_StillReadsAFileTheBranchDeleted()
    {
        Git("rm", "b.txt");
        Commit("drop b");

        using var json = InvokeOk("get_file_at_base", """{"path":"b.txt"}""");

        Assert.Equal("kept\n", json.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void GetFileAtBase_RefusesATraversalOutOfTheRepository()
    {
        var invocation = Invoke("get_file_at_base", """{"path":"../a.txt"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("outside the repository", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileAtBase_RefusesAnAbsolutePath()
    {
        var absolute = JsonSerializer.Serialize(Path.Combine(_root, "a.txt"));
        var invocation = Invoke("get_file_at_base", $$"""{"path":{{absolute}}}""");

        Assert.True(invocation.IsError);
        Assert.Contains("outside the repository", invocation.Content, StringComparison.Ordinal);
    }

    // A file of very long lines is capped by weight, not by line count: asking for the whole thing
    // returns a prefix and says it is a prefix.
    [Fact]
    public void GetFileAtBase_CapsWhatOneResultWeighs()
    {
        using var json = InvokeOk("get_file_at_base", """{"path":"big.txt","line_count":1200}""");
        var root = json.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(BigLineCount + 1, root.GetProperty("total_lines").GetInt32());
        Assert.True(root.GetProperty("end_line").GetInt32() < BigLineCount);
        Assert.True(root.GetProperty("content").GetString()!.Length <= 96 * 1024);
    }

    [Fact]
    public void GetFileAtBase_SaysSoForAFileTheBranchAdds()
    {
        var invocation = Invoke("get_file_at_base", """{"path":"c.txt"}""");
        Assert.True(invocation.IsError);
        Assert.Contains("c.txt", invocation.Content, StringComparison.Ordinal);
    }

    // Through the store, not around it: the same call the reviewer's checkbox makes, with the same
    // key and the same content fingerprint.
    [Fact]
    public void MarkViewed_WritesThroughTheReviewProgressStore()
    {
        var invocation = Invoke("mark_viewed", """{"paths":["a.txt"]}""");
        Assert.False(invocation.IsError, invocation.Content);

        var contentId = ContentIdOf("a.txt");
        Assert.True(_progress.IsViewed(_repo.Id, "feature", "a.txt", contentId));
    }

    [Fact]
    public void MarkViewed_ClearsAMarkWhenAskedTo()
    {
        Invoke("mark_viewed", """{"paths":["a.txt"]}""");
        Assert.False(Invoke("mark_viewed", """{"paths":["a.txt"],"viewed":false}""").IsError);

        Assert.False(_progress.IsViewed(_repo.Id, "feature", "a.txt", ContentIdOf("a.txt")));
    }

    // The mark carries the content it was made against, so amending the file re-opens it for review
    // exactly as a human's mark would. This is the reason it goes through the store.
    [Fact]
    public void AMarkMadeByTheAssistant_ExpiresWhenTheFileChangesAgain()
    {
        Assert.False(Invoke("mark_viewed", """{"paths":["a.txt"]}""").IsError);
        var before = ContentIdOf("a.txt");
        Assert.True(_progress.IsViewed(_repo.Id, "feature", "a.txt", before));

        Write("a.txt", "one\nTWO\nTHREE\n");
        Git("add", ".");
        Commit("change a again");

        var after = ContentIdOf("a.txt");
        Assert.NotEqual(before, after);
        Assert.False(_progress.IsViewed(_repo.Id, "feature", "a.txt", after));
    }

    // A review window open on the branch is bound to the tracker, not to the store, so a mark it did
    // not make itself has to reach it: without the store's own revision the checkboxes, the viewed
    // count and the HUD would keep drawing what they last read.
    [Fact]
    public void AMarkMadeByTheAssistant_ReachesAReviewWindowAlreadyBoundToTheBranch()
    {
        using var tracker = new BranchReviewedFiles(_progress, _repo.Id, "feature");
        tracker.SetFingerprints(new Dictionary<string, string?> { ["a.txt"] = ContentIdOf("a.txt") });
        var refreshes = 0;
        using var watch = tracker.Revision.Subscribe(_ => refreshes++);
        var before = refreshes;
        Assert.False(tracker.IsViewed("a.txt"));

        Assert.False(Invoke("mark_viewed", """{"paths":["a.txt"]}""").IsError);

        Assert.True(tracker.IsViewed("a.txt"));
        Assert.True(refreshes > before);
    }

    [Fact]
    public void MarkViewed_RefusesAPathThatIsNotInTheReview()
    {
        var invocation = Invoke("mark_viewed", """{"paths":["b.txt"]}""");

        Assert.True(invocation.IsError);
        Assert.Contains("b.txt", invocation.Content, StringComparison.Ordinal);
    }

    // Detachment is a fact about the checkout, so it is the checkout that gets detached here: the
    // record still names "feature", and a leftover branch name must not stand in for a review.
    [Fact]
    public void ADetachedHead_HasNoReviewAndSaysSoRatherThanGuessing()
    {
        Git("checkout", "--detach", "HEAD");

        var invocation = Invoke("get_review_stack");

        Assert.Equal("feature", _repo.Branch);
        Assert.True(invocation.IsError);
        Assert.Contains("detached", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    // The fingerprint the tool must have used: the range's own after-side content identity per file,
    // which is what the review window hands the tracker.
    private string? ContentIdOf(string path)
    {
        var (scope, error) = ReviewScope.Resolve(_git, _repo, null);
        Assert.Null(error);
        return scope!.File(path)?.ContentId;
    }
}
