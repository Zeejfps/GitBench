using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The conflict tools against a repository git really did leave half-merged: the list of what is
/// conflicted, one path's three sides in full, and the resolution that writes a side or the model's
/// own merged text and stages it.
/// </summary>
/// <remarks>
/// Everything here runs against the real index because the shapes that matter — a delete/modify with
/// no theirs blob, an add/add with no base, a path that is not unmerged at all — are exactly the ones
/// a stubbed git would have to be told about in advance.
/// </remarks>
public sealed class AssistantConflictToolsTests : IDisposable
{
    private readonly ConflictedRepo _merge = ConflictedRepo.Merging();
    private readonly ScriptedRemoteGitService _git = new(new GitService(new RepoActivityTracker()));
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly TempDir _state = new("gitbench-conflict-state-");
    private readonly AssistantToolset _toolset;

    public AssistantConflictToolsTests()
    {
        _toolset = ToolsetFor(_merge.Repo);
    }

    public void Dispose()
    {
        _merge.Dispose();
        _state.Dispose();
    }

    private AssistantToolset ToolsetFor(Repo repo)
    {
        var statePath = Path.Combine(_state.Path, "repos.json");
        var surface = new AssistantWriteSurface(
            _dispatcher,
            _bus,
            new RepoRegistry(RepoStateStore.Load(statePath), statePath),
            new SilentCommitEditor(),
            new IdleRemoteOperations());
        return AssistantToolset.Create(
            ConflictTools.CreateAll(_git, repo, surface),
            ["get_conflict", "get_conflicts", "resolve_conflict"]);
    }

    // ---- the toolset itself (AC-4.1 / AC-5.1 / AC-6.1) ----

    [Fact]
    public void OnlyResolveConflictNeedsApproval_AndEverySchemaIsValidJson()
    {
        Assert.Equal(
            new[] { "get_conflict", "get_conflicts", "resolve_conflict" },
            _toolset.Tools.Select(t => t.Name));
        Assert.Equal(
            new[] { "resolve_conflict" },
            _toolset.Tools.Where(t => t.IsWrite).Select(t => t.Name));
        Assert.All(_toolset.Tools, t => JsonDocument.Parse(t.JsonSchema).Dispose());
    }

    [Fact]
    public void ResolveConflict_OffersOursTheirsAndContentAndDeliberatelyNotBoth()
    {
        var schema = JsonDocument.Parse(_toolset.Find("resolve_conflict")!.JsonSchema);

        var resolutions = schema.RootElement
            .GetProperty("properties").GetProperty("resolution").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "ours", "theirs", "content" }, resolutions);
    }

    // ---- get_conflicts (AC-4) ----

    [Fact]
    public void GetConflicts_ListsEveryConflictedPathWithWhatEachSideDid()
    {
        var result = Json(Invoke("get_conflicts", "{}"));

        var byPath = result.GetProperty("conflicts").EnumerateArray()
            .ToDictionary(c => c.GetProperty("path").GetString()!, c => c);

        Assert.Equal(
            new[] { "a.txt", "fresh.txt", "gone.txt", "logo.bin", "long.txt", "naïve name.txt" },
            byPath.Keys);
        Assert.Equal("modified", byPath["a.txt"].GetProperty("ours").GetString());
        Assert.Equal("modified", byPath["a.txt"].GetProperty("theirs").GetString());
        Assert.Equal("added", byPath["fresh.txt"].GetProperty("ours").GetString());
        Assert.Equal("modified", byPath["gone.txt"].GetProperty("ours").GetString());
        Assert.Equal("deleted", byPath["gone.txt"].GetProperty("theirs").GetString());
    }

    [Fact]
    public void GetConflicts_NamesTheOperationThatIsStuck()
    {
        var result = Json(Invoke("get_conflicts", "{}"));

        Assert.Equal("merge", result.GetProperty("operation").GetString());
    }

    // Nothing left to resolve is an answer, and it is the answer the model asks this question to
    // get. An error here would read as "the check failed".
    [Fact]
    public void GetConflicts_WhenNothingIsConflicted_IsOkWithAnEmptyList()
    {
        _merge.Git("merge", "--abort");

        var invocation = Invoke("get_conflicts", "{}");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Empty(Json(invocation).GetProperty("conflicts").EnumerateArray());
    }

    // One `git ls-files -u` for the whole repository, not one per path. Asking git per path is the
    // shape that quietly turns a ten-file conflict into ten processes.
    [Fact]
    public void GetConflicts_AsksForTheWholeListOnceRatherThanPerPath()
    {
        Invoke("get_conflicts", "{}");

        Assert.Equal(1, _git.ConflictListings);
        Assert.Equal(0, _git.ConflictContexts);
    }

    // ---- get_conflict (AC-5) ----

    [Fact]
    public void GetConflict_ReturnsBaseOursAndTheirsInFull()
    {
        var result = Json(Invoke("get_conflict", """{"path":"a.txt"}"""));

        Assert.Equal("a.txt", result.GetProperty("path").GetString());
        Assert.Equal("merge", result.GetProperty("operation").GetString());
        Assert.Equal("one\ntwo\nthree\n", result.GetProperty("base").GetProperty("text").GetString());
        Assert.Equal("one\nfrom main\nthree\n", result.GetProperty("ours").GetProperty("text").GetString());
        Assert.Equal("one\nfrom feature\nthree\n", result.GetProperty("theirs").GetProperty("text").GetString());
    }

    [Fact]
    public void GetConflict_LabelsWhichBranchEachSideIs()
    {
        var result = Json(Invoke("get_conflict", """{"path":"a.txt"}"""));

        Assert.Equal("main", result.GetProperty("ours").GetProperty("label").GetString());
        Assert.Contains("feature", result.GetProperty("theirs").GetProperty("label").GetString());
    }

    // The side that deleted the file has no text to hand over, and "deleted" is a different fact
    // from "empty" — a model told the side is empty will happily write an empty file back.
    [Fact]
    public void GetConflict_WhenTheirSideDeletedTheFile_SaysDeletedAndOmitsItsText()
    {
        var theirs = Json(Invoke("get_conflict", """{"path":"gone.txt"}""")).GetProperty("theirs");

        Assert.Equal("deleted", theirs.GetProperty("change").GetString());
        Assert.False(theirs.TryGetProperty("text", out _));
    }

    // The case RepoFileGuard.Resolve would have refused outright: the path is not in the working
    // tree on the side that deleted it, and it is still a conflict the model has to settle.
    [Fact]
    public void GetConflict_OnAFileOneSideDeleted_IsStillReadable()
    {
        var invocation = Invoke("get_conflict", """{"path":"gone.txt"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal(
            "still here, and edited\n",
            Json(invocation).GetProperty("ours").GetProperty("text").GetString());
    }

    [Fact]
    public void GetConflict_OnAnAddAddConflict_HasNoBase()
    {
        var result = Json(Invoke("get_conflict", """{"path":"fresh.txt"}"""));

        Assert.False(result.TryGetProperty("base", out _));
        Assert.Equal("added", result.GetProperty("ours").GetProperty("change").GetString());
        Assert.Equal("added", result.GetProperty("theirs").GetProperty("change").GetString());
    }

    [Fact]
    public void GetConflict_OnAPathThatIsNotConflicted_SaysSoRatherThanReturningTheFile()
    {
        var invocation = Invoke("get_conflict", """{"path":"quiet.txt"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("quiet.txt", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConflict_WithoutAPath_IsAnErrorResultRatherThanAnException()
    {
        var invocation = Invoke("get_conflict", "{}");

        Assert.True(invocation.IsError);
        Assert.Contains("path", invocation.Content, StringComparison.Ordinal);
    }

    // Dropping the working-tree requirement is not the same as dropping the guard: a conflicted
    // .env is still a secret, and a conflict is a very good excuse to ask for one.
    [Fact]
    public void GetConflict_OnACredentialShapedPath_IsStillRefused()
    {
        var invocation = Invoke("get_conflict", """{"path":".env"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("credential", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetConflict_OnAPathThatLeavesTheRepository_IsRefused()
    {
        var invocation = Invoke("get_conflict", """{"path":"../elsewhere.txt"}""");

        Assert.True(invocation.IsError);
    }

    // One conflicted file must not be able to spend the whole turn's budget, and a side that was
    // cut short has to say so or the model merges against half a file believing it is whole.
    [Fact]
    public void GetConflict_WhenASideIsLongerThanTheCap_CutsItShortAndSaysSo()
    {
        var ours = Json(Invoke("get_conflict", """{"path":"long.txt"}""")).GetProperty("ours");

        Assert.True(ours.GetProperty("truncated").GetBoolean());
        var lines = ours.GetProperty("text").GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(GetConflictTool.MaxSideLines, lines.Length);
        Assert.Equal("main line 1", lines[0]);
    }

    [Fact]
    public void GetConflict_OnASideThatFitsUnderTheCap_DoesNotClaimItWasCutShort()
    {
        var ours = Json(Invoke("get_conflict", """{"path":"a.txt"}""")).GetProperty("ours");

        Assert.False(ours.GetProperty("truncated").GetBoolean());
    }

    // Decoded bytes are not text. Handing a conflicted PNG back as several thousand replacement
    // characters costs the turn and tells the model nothing it can merge.
    [Fact]
    public void GetConflict_OnABinaryConflict_SaysSoInsteadOfReturningTheBytes()
    {
        var invocation = Invoke("get_conflict", """{"path":"logo.bin"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("binary", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    // A rebase replays your commits onto theirs, so "ours" is the branch being rebased onto — the
    // model has to be told which operation it is standing in to read the sides correctly.
    [Fact]
    public void GetConflict_DuringARebase_NamesTheOperationRatherThanCallingItAMerge()
    {
        using var rebase = ConflictedRepo.Rebasing();

        var result = Json(Invoke(ToolsetFor(rebase.Repo), "get_conflict", """{"path":"a.txt"}"""));

        Assert.Equal("rebase", result.GetProperty("operation").GetString());
        Assert.Equal("one\nfrom main\nthree\n", result.GetProperty("ours").GetProperty("text").GetString());
        Assert.Equal("one\nfrom feature\nthree\n", result.GetProperty("theirs").GetProperty("text").GetString());
    }

    // ---- resolve_conflict (AC-6) ----

    [Fact]
    public void ResolveConflict_WithOurs_KeepsOurSideAndStagesIt()
    {
        var invocation = Invoke("resolve_conflict", """{"path":"a.txt","resolution":"ours"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("one\nfrom main\nthree\n", _merge.Read("a.txt"));
        Assert.DoesNotContain("a.txt", _merge.UnmergedIndex());
    }

    [Fact]
    public void ResolveConflict_WithTheirs_KeepsTheirSideAndStagesIt()
    {
        var invocation = Invoke("resolve_conflict", """{"path":"a.txt","resolution":"theirs"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("one\nfrom feature\nthree\n", _merge.Read("a.txt"));
        Assert.DoesNotContain("a.txt", _merge.UnmergedIndex());
    }

    // Taking the side that deleted the file means the file goes, not that an empty one stays.
    [Fact]
    public void ResolveConflict_WithTheirs_WhenTheirSideDeletedTheFile_RemovesIt()
    {
        var invocation = Invoke("resolve_conflict", """{"path":"gone.txt","resolution":"theirs"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.False(_merge.Exists("gone.txt"));
        Assert.DoesNotContain("gone.txt", _merge.UnmergedIndex());
    }

    [Fact]
    public void ResolveConflict_WithContent_WritesTheMergedTextAndStagesIt()
    {
        var invocation = Invoke(
            "resolve_conflict",
            """{"path":"a.txt","resolution":"content","content":"one\nfrom both\nthree\n"}""");

        Assert.False(invocation.IsError, invocation.Content);
        Assert.Equal("one\nfrom both\nthree\n", _merge.Read("a.txt"));
        Assert.DoesNotContain("a.txt", _merge.UnmergedIndex());
    }

    // What is staged has to be what was written; a resolution that lands on disk but not in the
    // index leaves the merge stuck with no sign of why.
    [Fact]
    public void ResolveConflict_WithContent_StagesTheSameTextItWrote()
    {
        Invoke("resolve_conflict", """{"path":"a.txt","resolution":"content","content":"merged\n"}""");

        Assert.Equal("merged\n", _merge.Git("show", ":a.txt"));
    }

    // Git files often deliberately end without one; adding a newline the model did not write is a
    // change to their file nobody asked for.
    [Fact]
    public void ResolveConflict_WithContent_WritesItVerbatimWithoutAddingATrailingNewline()
    {
        Invoke("resolve_conflict", """{"path":"a.txt","resolution":"content","content":"no trailing newline"}""");

        Assert.Equal("no trailing newline", _merge.Read("a.txt"));
    }

    [Fact]
    public void ResolveConflict_WithContentButNoText_IsRefusedWithoutTouchingTheFile()
    {
        var invocation = Invoke("resolve_conflict", """{"path":"a.txt","resolution":"content"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("content", invocation.Content, StringComparison.Ordinal);
        Assert.Contains("a.txt", _merge.UnmergedIndex());
    }

    // 'both' is the one a model reaches for and the one that is almost always wrong; the refusal
    // has to say what to do instead rather than only that it is not on the list.
    [Fact]
    public void ResolveConflict_WithAResolutionItDoesNotOffer_NamesTheOnesItDoes()
    {
        var invocation = Invoke("resolve_conflict", """{"path":"a.txt","resolution":"both"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("content", invocation.Content, StringComparison.Ordinal);
        Assert.Contains("a.txt", _merge.UnmergedIndex());
    }

    [Fact]
    public void ResolveConflict_OnAPathThatIsNotConflicted_IsRefusedRatherThanOverwritingIt()
    {
        var invocation = Invoke(
            "resolve_conflict",
            """{"path":"quiet.txt","resolution":"content","content":"clobbered"}""");

        Assert.True(invocation.IsError);
        Assert.Equal("neither side touched this\n", _merge.Read("quiet.txt"));
    }

    [Fact]
    public void ResolveConflict_OnACredentialShapedPath_IsStillRefused()
    {
        var invocation = Invoke("resolve_conflict", """{"path":".env","resolution":"content","content":"x"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("credential", invocation.Content, StringComparison.OrdinalIgnoreCase);
    }

    // The same broadcast the Resolve buttons make, on the thread the view models live on — without
    // it the file lists and the open diff keep painting the conflict that is no longer there.
    [Fact]
    public void ResolveConflict_TellsTheAppTheWorkingTreeMoved()
    {
        var told = new List<WorkingTreeChangedMessage>();
        _bus.Subscribe<WorkingTreeChangedMessage>(told.Add);

        Invoke("resolve_conflict", """{"path":"a.txt","resolution":"ours"}""");

        var moved = Assert.Single(told);
        Assert.Equal(_merge.Repo.Id, moved.RepoId);
        Assert.False(moved.IndexOnly);
    }

    [Fact]
    public void ResolveConflict_BroadcastsOnlyAfterTheResolutionLanded()
    {
        var stagedWhenTold = new List<string>();
        _bus.Subscribe<WorkingTreeChangedMessage>(_ => stagedWhenTold.Add(_merge.UnmergedIndex()));

        Invoke("resolve_conflict", """{"path":"a.txt","resolution":"ours"}""");

        Assert.DoesNotContain("a.txt", Assert.Single(stagedWhenTold));
    }

    // Whether anything is left is the question the model asks next anyway, and answering it here
    // saves a round trip on every file of a multi-file conflict.
    [Fact]
    public void ResolveConflict_ReportsHowManyConflictsAreStillOpen()
    {
        var before = Json(Invoke("get_conflicts", "{}")).GetProperty("conflicts").GetArrayLength();

        var result = Json(Invoke("resolve_conflict", """{"path":"a.txt","resolution":"ours"}"""));

        Assert.Equal(before - 1, result.GetProperty("conflicts_remaining").GetInt32());
    }

    [Fact]
    public void ResolveConflict_ReachesTheUiThreadRatherThanBroadcastingFromTheCallingOne()
    {
        var tool = _toolset.Find("resolve_conflict")!;
        var told = 0;
        _bus.Subscribe<WorkingTreeChangedMessage>(_ => told++);

        var task = tool.InvokeAsync(
            AssistantTestJson.Element("""{"path":"a.txt","resolution":"ours"}"""),
            CancellationToken.None);

        Assert.Equal(0, told);
        Assert.True(_dispatcher.Queued > 0, "the broadcast should be posted and still pending");

        Pump.WaitFor(_dispatcher, () => task.IsCompleted, "the resolve_conflict tool to finish");

        Assert.Equal(1, told);
    }

    private ToolInvocation Invoke(string tool, string args) => Invoke(_toolset, tool, args);

    private ToolInvocation Invoke(AssistantToolset toolset, string tool, string args)
    {
        var instance = toolset.Find(tool);
        Assert.NotNull(instance);
        var task = instance!.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, $"the {tool} tool to finish");
        return task.GetAwaiter().GetResult();
    }

    private static JsonElement Json(ToolInvocation invocation)
    {
        Assert.False(invocation.IsError, invocation.Content);
        using var document = JsonDocument.Parse(invocation.Content);
        return document.RootElement.Clone();
    }
}
