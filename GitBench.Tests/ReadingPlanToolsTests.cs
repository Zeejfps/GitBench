using System.Text.Json;
using GitBench.Features.Diff.Reading;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// The tools are the whole surface a model reaches this feature through: a plan it can check, and one
// it commits to. A rejected plan has to come back as an error the model can answer rather than a
// dead run, and an accepted one has to be the plan that reaches the cache.
public class ReadingPlanToolsTests
{
    private static DiffResult File(string path, params DiffLine[] lines)
        => new(
            RepoId: Guid.Empty,
            Path: path,
            OldPath: null,
            Side: DiffSide.Commit,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: [new DiffHunk(1, lines.Length, 1, lines.Length, null, lines)],
            Truncated: false,
            ErrorMessage: null);

    private static DiffLine Add(string text) => new(DiffLineKind.Added, null, 1, text);

    private static ReadingAbridgement Abridgement() => new(ReadingRowIndex.Build([
        File("a.cs", Add("using System;"), Add("var x = 1;"), Add("var y = 2;"), Add("var z = 3;")),
    ]));

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task PreviewReportsRetentionAndTheResultingDiff()
    {
        var tool = new PreviewPlanTool(Abridgement());

        var result = await tool.InvokeAsync(
            Args("""{"remove":[{"start_row":4,"end_row":4}],"replace":[],"fold":[]}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        // 4 changed rows in, 2 shown: the import counts against the total even though nobody chose
        // to hide it, because the denominator is what the reader is being asked to trust.
        Assert.Contains("2/4 changed rows", result.Content);
        Assert.Contains("+var x = 1;", result.Content);
        Assert.DoesNotContain("var z = 3;", result.Content);
        // The import went without being asked for, and never appears in what the model is shown.
        Assert.DoesNotContain("using System;", result.Content);
    }

    [Fact]
    public async Task PreviewRejectsAnInvalidPlanWithAReasonRatherThanFailing()
    {
        var tool = new PreviewPlanTool(Abridgement());

        var result = await tool.InvokeAsync(
            Args("""{"remove":[],"replace":[{"row":2,"old":"var x = 1;","new":"var q = 9;…"}],"fold":[]}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not an elision", result.Content);
    }

    [Fact]
    public async Task PreviewLeavesTheRunWithNoResult()
    {
        var abridgement = Abridgement();

        await new PreviewPlanTool(abridgement).InvokeAsync(
            Args("""{"remove":[],"replace":[],"fold":[]}"""), CancellationToken.None);

        Assert.Null(abridgement.Result);
    }

    [Fact]
    public async Task SubmitKeepsBothThePlanAndItsCompiledResult()
    {
        var abridgement = Abridgement();

        var result = await new SubmitPlanTool(abridgement).InvokeAsync(
            Args("""{"remove":[{"start_row":3,"end_row":4}],"replace":[],"fold":[],"summary":"Adds x."}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(abridgement.Result);
        Assert.Equal("Adds x.", abridgement.Result!.Summary);
        Assert.True(abridgement.Result.IsHidden(3));
        Assert.NotNull(abridgement.Plan);
        Assert.Single(abridgement.Plan!.Remove);
    }

    [Fact]
    public async Task SubmitNeedsASummary()
    {
        var abridgement = Abridgement();

        var result = await new SubmitPlanTool(abridgement).InvokeAsync(
            Args("""{"remove":[],"replace":[],"fold":[]}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(abridgement.Result);
    }

    // A rejected submission must not half-land: the run carries on and the model gets to fix it.
    [Fact]
    public async Task ARejectedSubmissionLeavesNothingBehind()
    {
        var abridgement = Abridgement();

        var result = await new SubmitPlanTool(abridgement).InvokeAsync(
            Args("""{"remove":[{"start_row":1,"end_row":99}],"replace":[],"fold":[],"summary":"x"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(abridgement.Result);
        Assert.Null(abridgement.Plan);
    }

    [Fact]
    public async Task MissingArrayArgumentsAreNamedRatherThanAssumedEmpty()
    {
        var result = await new PreviewPlanTool(Abridgement()).InvokeAsync(
            Args("""{"remove":[]}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'fold' must be an array", result.Content);
    }

    // The cache is keyed on this, so a change to the rubric, a tool description, the schema or the
    // feedback wording has to move it — otherwise a stale plan is served under new rules.
    [Fact]
    public void TheSurfaceHashCoversTheRubric()
    {
        var one = ReadingSurface.Hash("first rubric");
        var two = ReadingSurface.Hash("second rubric");

        Assert.NotEqual(one, two);
        Assert.Equal(one, ReadingSurface.Hash("first rubric"));
    }

    [Fact]
    public void PlansRoundTripThroughTheStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reading-plans-" + Guid.NewGuid().ToString("N"));
        try
        {
            var abridgement = Abridgement();
            var store = new ReadingPlanStore(dir);
            var plan = new ReadingPlan(
                [new ReadingRemoval(4, 4)],
                [new ReadingElision(2, "var x = 1;", "var x…")],
                [],
                "Adds x.");

            store.Save("key", plan);
            var loaded = store.Load("key", abridgement.Index);

            Assert.NotNull(loaded);
            Assert.True(loaded!.IsHidden(4));
            Assert.Equal("var x…", loaded.ElidedText(2));
            Assert.Equal("Adds x.", loaded.Summary);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AMissingCacheEntryIsSimplyAMiss()
    {
        var store = new ReadingPlanStore(Path.Combine(Path.GetTempPath(), "reading-plans-none-" + Guid.NewGuid()));

        Assert.Null(store.Load("nothing-here", Abridgement().Index));
    }
}
