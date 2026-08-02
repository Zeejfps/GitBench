using GitBench.Features.Assistant;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The transcript line's argument summary. What it has to get right is the trade it exists for:
/// enough to answer "on what?" at a glance, never enough to grow the row it is drawn on.
/// </summary>
public sealed class ToolCallSummaryTests
{
    [Fact]
    public void Summary_LeadsWithTheThingBeingAddressed()
    {
        Assert.Equal("src/Git/GitService.cs", Describe("""{"path":"src/Git/GitService.cs"}"""));
        Assert.Equal("src/a.cs unstaged", Describe("""{"path":"src/a.cs","side":"unstaged"}"""));
    }

    // A bare number says nothing on its own, so it keeps its name where a path does not need one.
    [Fact]
    public void Summary_NamesNumbersAndFlags()
    {
        Assert.Equal(
            "src/a.cs start_line=5 line_count=3",
            Describe("""{"path":"src/a.cs","start_line":5,"line_count":3}"""));
        Assert.Equal("limit=40", Describe("""{"limit":40}"""));
        Assert.Equal("all", Describe("""{"all":true}"""));
        Assert.Equal("no all", Describe("""{"all":false}"""));
    }

    [Fact]
    public void Summary_CountsPastTheFirstFewArrayEntries()
    {
        Assert.Equal("a.cs, b.cs", Describe("""{"paths":["a.cs","b.cs"]}"""));
        Assert.Equal("a.cs, b.cs, c.cs +2", Describe("""{"paths":["a.cs","b.cs","c.cs","d.cs","e.cs"]}"""));
    }

    // A commit message is many lines of prose; the row it lands on is one line high.
    [Fact]
    public void Summary_FlattensAndClipsProse()
    {
        var summary = Describe("""{"message":"Fix the thing\n\nIt was broken because of a long story that goes on."}""");
        Assert.NotNull(summary);
        Assert.DoesNotContain('\n', summary);
        Assert.True(summary.Length <= 64, summary);
        Assert.StartsWith("Fix the thing It was broken", summary);
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void Summary_StaysWithinTheLine()
    {
        var summary = Describe($$"""{"path":"{{new string('a', 200)}}","side":"unstaged"}""");
        Assert.NotNull(summary);
        Assert.True(summary.Length <= 64, summary);
    }

    // A tool that takes nothing gets no separator and no empty tail on its line.
    [Fact]
    public void Summary_IsAbsentWhenThereIsNothingToSay()
    {
        Assert.Null(Describe("{}"));
        Assert.Null(Describe("""{"path":"   "}"""));
        Assert.Null(Describe("[]"));
    }

    private static string? Describe(string json) => ToolCallSummary.Describe(AssistantTestJson.Element(json));
}
