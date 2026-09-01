using GitBench.Features.Diff;
using GitBench.Theming;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The routing contract, which is the thing that makes swapping engines safe: a language reaches
/// tree-sitter only when it has a query, and every other path — an unbundled language, a file
/// tree-sitter refuses — still lands on TextMate rather than on plain text.
/// </summary>
[Collection(nameof(TreeSitterHighlightCollection))]
public class RoutedSyntaxHighlighterTests(TreeSitterHighlightFixture fixture)
{
    [Fact]
    public void ABundledLanguageIsColoredByTheParser()
    {
        var textMate = new RecordingHighlighter();
        var routed = new RoutedSyntaxHighlighter(fixture.Highlighter, textMate);

        var spans = routed.Highlight("class Box { void Run() { } }", "csharp");

        Assert.NotNull(spans);
        Assert.Equal(0, textMate.Calls);
        Assert.True(routed.RoutesToTreeSitter("csharp"));
    }

    [Fact]
    public void MarkdownAndHtmlAreColoredByTheParserToo()
    {
        var textMate = new RecordingHighlighter();
        var routed = new RoutedSyntaxHighlighter(fixture.Highlighter, textMate);

        // The two that spent a phase on TextMate: their queries are almost entirely injections, so
        // routing them was the injection engine landing rather than a query being written.
        routed.Highlight("# heading\n\n**bold**\n\n```json\n{ \"a\": 1 }\n```", "markdown");
        routed.Highlight("<p class=\"x\">hi</p>\n<script>const a = 1;</script>", "html");

        Assert.Equal(0, textMate.Calls);
        Assert.True(routed.RoutesToTreeSitter("markdown"));
        Assert.True(routed.RoutesToTreeSitter("html"));
    }

    [Fact]
    public void AnUnbundledLanguageStillReachesTextMate()
    {
        var textMate = new RecordingHighlighter();
        var routed = new RoutedSyntaxHighlighter(fixture.Highlighter, textMate);

        routed.Highlight("let x = 1", "fsharp");

        Assert.Equal(1, textMate.Calls);
    }

    /// <summary>The guarantee the whole design rests on: a file tree-sitter declines is not a file
    /// that renders plain, it is a file TextMate colors exactly as it does today.</summary>
    [Fact]
    public void AFileTreeSitterDeclinesFallsBackRatherThanGoingPlain()
    {
        var textMate = new RecordingHighlighter();
        var routed = new RoutedSyntaxHighlighter(fixture.Highlighter, textMate);

        var overCap = new string('a', TreeSitterSyntaxHighlighter.MaxFileBytes + 1);
        Assert.Null(fixture.Highlighter.Highlight(overCap, "csharp"));

        var spans = routed.Highlight(overCap, "csharp");

        Assert.Equal(1, textMate.Calls);
        Assert.NotNull(spans);
    }

    [Fact]
    public void BothEnginesDecliningIsTheOnlyWayToRenderPlain()
    {
        var routed = new RoutedSyntaxHighlighter(fixture.Highlighter, new NullHighlighter());
        Assert.Null(routed.Highlight("x", "nonsense-language"));
    }

    private sealed class RecordingHighlighter : ISyntaxHighlighter
    {
        public int Calls { get; private set; }

        public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId)
        {
            Calls++;
            return [[new TokenSpan(0, 1, TokenColorSlot.Keyword)]];
        }
    }

    private sealed class NullHighlighter : ISyntaxHighlighter
    {
        public IReadOnlyList<IReadOnlyList<TokenSpan>>? Highlight(string fileText, string languageId) => null;
    }
}
