using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using GitBench.Features.Diff;
using GitBench.Features.Review;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The review presentation tools end to end: a narrator opens the window, points at a line, lights
/// ranges up and reads the state back — through the same UI-thread hop and the same mounted list
/// the reviewer looks at.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class ReviewPresentationToolsTests : IDisposable
{
    private readonly ReviewPresentationFixture _fixture = new();
    private readonly IReadOnlyList<IAssistantTool> _tools;

    public ReviewPresentationToolsTests()
    {
        _tools = ReviewPresentationTools.CreateAll(_fixture.Git, _fixture.Repo, _fixture.Windows, _fixture.Surface());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void TheToolsChangeTheScreenNotTheRepository()
    {
        Assert.Equal(
            ["review_state", "review_open", "review_focus", "review_spotlight", "review_clear"],
            _tools.Select(t => t.Name));
        Assert.All(_tools, t => Assert.False(t.IsWrite));
        Assert.All(_tools, t => JsonDocument.Parse(t.JsonSchema).Dispose());
    }

    [Fact]
    public void ReviewOpen_OpensTheWindowOnceAndFocusesItAfter()
    {
        var focused = 0;
        _fixture.Windows.FocusRequested += _ => focused++;

        using var first = InvokeOk("review_open", """{"head":"feature","base":"main"}""");
        Assert.Equal("feature", first.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", first.RootElement.GetProperty("base").GetString());
        Assert.Equal("feature", Assert.Single(_fixture.Windows.Windows).Session.HeadRef);
        Assert.Equal(0, focused);

        using var again = InvokeOk("review_open", """{"head":"feature"}""");
        Assert.Single(_fixture.Windows.Windows);
        Assert.Equal(1, focused);
    }

    // Head defaults to the checked-out branch (feature), and the base to what the review window
    // itself would resolve — here main, the default branch, since feature has no upstream.
    [Fact]
    public void ReviewOpen_DefaultsToTheCheckedOutBranchAndItsResolvedBase()
    {
        using var json = InvokeOk("review_open");

        Assert.Equal("feature", json.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", json.RootElement.GetProperty("base").GetString());
    }

    [Fact]
    public void ReviewOpen_RefusesARangeThatDoesNotResolve()
    {
        var invocation = Invoke("review_open", """{"head":"feature","base":"no-such-branch"}""");

        Assert.True(invocation.IsError);
        Assert.Contains("no-such-branch", invocation.Content, StringComparison.Ordinal);
        Assert.Empty(_fixture.Windows.Windows);
    }

    [Fact]
    public void ReviewFocus_WithoutAWindowNamesReviewOpen()
    {
        var invocation = Invoke("review_focus", """{"path":"a.txt","line":5}""");

        Assert.True(invocation.IsError);
        Assert.Contains("review_open", invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewFocus_QuotesTheLineItLandedOn()
    {
        MountWindow();

        using var json = InvokeOk("review_focus", """{"path":"a.txt","line":5}""");

        Assert.Equal("a.txt", json.RootElement.GetProperty("path").GetString());
        Assert.Equal("new", json.RootElement.GetProperty("side").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("line").GetInt32());
        Assert.Equal("a line 5 changed", json.RootElement.GetProperty("text").GetString());

        using var old = InvokeOk("review_focus", """{"path":"a.txt","line":5,"side":"old"}""");
        Assert.Equal("a line 5", old.RootElement.GetProperty("text").GetString());
    }

    // Without a line the file's header is shown and the first line the diff holds is what comes
    // back — where the change begins.
    [Fact]
    public void ReviewFocus_OnAFileReportsWhereItsDiffStarts()
    {
        MountWindow();

        using var json = InvokeOk("review_focus", """{"path":"b.txt"}""");

        Assert.Equal("b.txt", json.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("line").GetInt32());
        Assert.Equal("b line 1", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void ReviewFocus_ReportsALineTheDiffLacksWithItsNeighbours()
    {
        MountWindow();

        var invocation = Invoke("review_focus", $$"""{"path":"a.txt","line":{{ReviewPresentationFixture.ALines + 1}}}""");

        Assert.True(invocation.IsError);
        Assert.Contains($"{ReviewPresentationFixture.ALines}", invocation.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"line":5}""", "path")]
    [InlineData("""{"path":"a.txt","line":0}""", "line")]
    [InlineData("""{"path":"a.txt","line":"five"}""", "line")]
    [InlineData("""{"path":"a.txt","line":5,"side":"left"}""", "side")]
    public void ReviewFocus_RefusesBadArguments(string args, string named)
    {
        MountWindow();

        var invocation = Invoke("review_focus", args);

        Assert.True(invocation.IsError);
        Assert.Contains(named, invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewSpotlight_ResolvesEveryRangeAndAppliesTheSet()
    {
        var window = MountWindow();

        using var json = InvokeOk(
            "review_spotlight",
            """{"spotlights":[{"path":"a.txt","from":6,"to":7,"note":"two context lines"},{"path":"a.txt","side":"old","from":35},{"path":"a.txt","from":99}],"dim":true}""");

        var results = json.RootElement.GetProperty("spotlights").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal("resolved", results[0].GetProperty("status").GetString());
        Assert.Equal("a line 6\na line 7", results[0].GetProperty("text").GetString());
        Assert.Equal(1, results[0].GetProperty("pin").GetInt32());
        Assert.Equal("resolved", results[1].GetProperty("status").GetString());
        Assert.Equal("a line 35", results[1].GetProperty("text").GetString());
        Assert.Equal("not_in_diff", results[2].GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("dim").GetBoolean());

        Assert.Equal(3, window.Spotlights.Value.Count);
        Assert.Equal("two context lines", window.Spotlights.Value[0].Note);
        Assert.True(window.SpotlightDim.Value);

        using var cleared = InvokeOk("review_clear");
        Assert.Empty(window.Spotlights.Value);
    }

    [Theory]
    [InlineData("""{"spotlights":"a.txt"}""", "spotlights")]
    [InlineData("""{"spotlights":[{"from":1}]}""", "path")]
    [InlineData("""{"spotlights":[{"path":"a.txt"}]}""", "from")]
    [InlineData("""{"spotlights":[{"path":"a.txt","from":7,"to":6}]}""", "before")]
    [InlineData("""{"spotlights":[{"path":"a.txt","from":6,"side":"both"}]}""", "side")]
    public void ReviewSpotlight_RefusesBadArguments(string args, string named)
    {
        MountWindow();

        var invocation = Invoke("review_spotlight", args);

        Assert.True(invocation.IsError);
        Assert.Contains(named, invocation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewState_DescribesTheOpenWindow()
    {
        MountWindow();
        InvokeOk("review_focus", """{"path":"a.txt","line":5}""").Dispose();

        using var json = InvokeOk("review_state");

        var window = Assert.Single(json.RootElement.GetProperty("windows").EnumerateArray());
        Assert.Equal("feature", window.GetProperty("head").GetString());
        Assert.Equal("main", window.GetProperty("base").GetString());
        Assert.Equal("a.txt", window.GetProperty("active_file").GetString());
        Assert.Equal("a.txt", window.GetProperty("visible").GetProperty("path").GetString());
        Assert.Empty(window.GetProperty("viewed").EnumerateArray());
        Assert.False(window.TryGetProperty("selection", out _));
    }

    private ReviewWindowViewModel MountWindow()
    {
        var window = _fixture.OpenWindow();
        _fixture.Mount(window);
        return window;
    }

    private JsonDocument InvokeOk(string tool, string args = "{}")
    {
        var invocation = Invoke(tool, args);
        Assert.False(invocation.IsError, invocation.Content);
        return JsonDocument.Parse(invocation.Content);
    }

    private ToolInvocation Invoke(string tool, string args = "{}")
    {
        var instance = Assert.Single(_tools, t => t.Name == tool);
        return _fixture.Await(instance.InvokeAsync(AssistantTestJson.Element(args), CancellationToken.None));
    }
}
