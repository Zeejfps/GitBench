using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Theming;

using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Injected languages: the regions a grammar hands to another one, which is what Markdown and HTML
/// are almost entirely made of.
/// </summary>
[Collection(nameof(TreeSitterHighlightCollection))]
public class TreeSitterInjectionTests(TreeSitterHighlightFixture fixture)
{
    [Fact]
    public void AScriptBodyIsColoredAsJavaScript()
    {
        const string source = "<p>hi</p>\n<script>\nconst total = 42;\n</script>";

        var spans = fixture.Highlighter.Highlight(source, "html");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "const"));
        Assert.Equal(TokenColorSlot.Number, TreeSitterHighlightTests.SlotOf(source, spans, "42"));
        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "script"));
    }

    [Fact]
    public void AStyleBodyIsColoredAsCss()
    {
        const string source = "<style>\n.card { color: red; }\n</style>";

        var spans = fixture.Highlighter.Highlight(source, "html");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Variable, TreeSitterHighlightTests.SlotOf(source, spans, "color"));
    }

    [Fact]
    public void AFencedBlockIsColoredInTheLanguageItsInfoStringNames()
    {
        const string source = "text\n\n```csharp\nclass Box { }\n```\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "class"));
        Assert.Equal(TokenColorSlot.Type, TreeSitterHighlightTests.SlotOf(source, spans, "Box"));
    }

    [Theory]
    [InlineData("js")]
    [InlineData("javascript")]
    [InlineData("JS")]
    public void AnInfoStringAliasResolvesToTheSameGrammar(string info)
    {
        var source = $"```{info}\nconst total = 42;\n```\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "const"));
    }

    [Fact]
    public void AFencedBlockInAnUnbundledLanguageIsLeftToItsFence()
    {
        const string source = "```brainfuck\n+++[->+++<]\n```\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);
        Assert.Equal(TokenColorSlot.Code, TreeSitterHighlightTests.SlotOf(source, spans, "+++["));
    }

    [Fact]
    public void InlineMarkdownIsColoredThroughTheInlineGrammar()
    {
        const string source = "A **bold** word, an *emphasized* one, some `code` and a [link](http://x.dev).\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Emphasis, TreeSitterHighlightTests.SlotOf(source, spans, "**bold**"));
        Assert.Equal(TokenColorSlot.Emphasis, TreeSitterHighlightTests.SlotOf(source, spans, "*emphasized*"));
        Assert.Equal(TokenColorSlot.Code, TreeSitterHighlightTests.SlotOf(source, spans, "`code`"));
        Assert.Equal(TokenColorSlot.Link, TreeSitterHighlightTests.SlotOf(source, spans, "http://x.dev"));
    }

    [Fact]
    public void HeadingsAndQuotesComeFromTheBlockGrammar()
    {
        const string source = "# Title\n\n> quoted\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Heading, TreeSitterHighlightTests.SlotOf(source, spans, "Title"));
    }

    [Fact]
    public void AnInjectionInsideAnInjectionIsFollowed()
    {
        const string source = "<div>\n<script>\nconst total = 42;\n</script>\n</div>\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "const"));
    }

    [Fact]
    public void YamlFrontMatterIsColoredAsYaml()
    {
        const string source = "---\ntitle: Post\n---\n\nBody text.\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Variable, TreeSitterHighlightTests.SlotOf(source, spans, "title"));
    }

    [Fact]
    public void AnInjectedRegionAfterNonAsciiTextLandsOnTheRightColumns()
    {
        const string source = "Héllo wörld 🎉 and more\n\n```json\n{ \"a\": 12 }\n```\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        Assert.Equal(TokenColorSlot.Number, TreeSitterHighlightTests.SlotOf(source, spans, "12"));
    }

    [Fact]
    public void SpansStayOrderedAndInsideTheirLineAcrossAnInjection()
    {
        const string source = "# Title\n\n```html\n<script>const a = 1;</script>\n```\n\n*done* — `x`\n";

        var spans = fixture.Highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);

        var lines = source.Split('\n');
        Assert.Equal(lines.Length, spans.Count);

        for (var i = 0; i < lines.Length; i++)
        {
            var width = DiffText.ExpandTabs(lines[i]).Length;
            var previousEnd = 0;

            foreach (var span in spans[i])
            {
                Assert.True(span.Length > 0, $"line {i + 1}: zero-length span");
                Assert.True(span.Start >= previousEnd, $"line {i + 1}: spans out of order or overlapping");
                Assert.True(span.Start + span.Length <= width, $"line {i + 1}: span runs past the line");
                previousEnd = span.Start + span.Length;
            }
        }
    }

    // A pool of one is what a nested Use would deadlock on.
    [Fact]
    public void InjectionsRunOnAPoolOfOne()
    {
        using var highlighter = new TreeSitterSyntaxHighlighter(poolCapacity: 1);
        const string source = "```csharp\nclass Box { }\n```\n";

        var spans = highlighter.Highlight(source, "markdown");
        Assert.NotNull(spans);
        Assert.Equal(TokenColorSlot.Keyword, TreeSitterHighlightTests.SlotOf(source, spans, "class"));
    }

    // The canary for a grammar pin bump: a query that stops compiling takes its language with it.
    [Fact]
    public void EveryBundledInjectionQueryCompiled()
    {
        var log = new List<string>();
        using var highlighter = new TreeSitterSyntaxHighlighter(log.Add);

        Assert.Empty(log);
        Assert.True(highlighter.Supports("markdown"));
        Assert.True(highlighter.Supports("html"));
    }

    [Fact]
    public void TheInlineGrammarIsNotALanguageAFileCanBe()
    {
        Assert.DoesNotContain(CodeLanguage.MarkdownInline, CodeLanguages.All);
        Assert.Contains(CodeLanguage.MarkdownInline, CodeLanguages.Bundled);
        Assert.Null(CodeLanguages.Detect("readme.markdown_inline"));
    }
}
