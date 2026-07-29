using GitBench.Features.Markdown.Parsing;
using Xunit;

namespace GitBench.Tests.Markdown;

/// <summary>
/// Step 2 wiring contract for <see cref="BasicMarkdownParser"/>: every inline-bearing block —
/// paragraphs, headings, table cells, and the paragraphs inside list items and quotes — must
/// route its raw text through <see cref="InlineParser"/>, while <see cref="CodeBlock"/> text
/// stays verbatim and is never inline-parsed. Expected run shapes follow the pins documented in
/// <see cref="InlineParserTests"/> (hard break = a lone unstyled "\n" run, adjacent same-style
/// runs merged). These are red until the block parser swaps its single-raw-run emission for
/// InlineParser output.
/// </summary>
public class BlockInlineIntegrationTests
{
    private static MarkdownDocument Parse(string markdown) => new BasicMarkdownParser().Parse(markdown);

    private static T SingleBlock<T>(string markdown) where T : MarkdownBlock
    {
        var doc = Parse(markdown);
        var block = Assert.Single(doc.Blocks);
        return Assert.IsType<T>(block);
    }

    [Fact]
    public void ParagraphTextIsInlineParsed()
    {
        var para = SingleBlock<ParagraphBlock>("**bold** para");
        Assert.Equal(
            new[] { new InlineRun("bold", Bold: true), new InlineRun(" para") },
            para.Runs);
    }

    [Fact]
    public void MultiLineParagraphIsInlineParsedAsOneText()
    {
        // Consecutive lines join before inline parsing, so emphasis may span the join and the
        // line ending resolves to a soft break's space.
        var para = SingleBlock<ParagraphBlock>("*a\nb* c");
        Assert.Equal(
            new[] { new InlineRun("a b", Italic: true), new InlineRun(" c") },
            para.Runs);
    }

    [Fact]
    public void HardBreakInParagraphBecomesNewlineRun()
    {
        var para = SingleBlock<ParagraphBlock>("line one  \nline two");
        Assert.Equal(
            new[] { new InlineRun("line one"), new InlineRun("\n"), new InlineRun("line two") },
            para.Runs);
    }

    [Fact]
    public void HeadingTextIsInlineParsed()
    {
        var heading = SingleBlock<HeadingBlock>("# *it*");
        Assert.Equal(1, heading.Level);
        Assert.Equal(new[] { new InlineRun("it", Italic: true) }, heading.Runs);
    }

    [Fact]
    public void HeadingWithCodeSpanIsInlineParsed()
    {
        var heading = SingleBlock<HeadingBlock>("## use `git rebase`");
        Assert.Equal(
            new[] { new InlineRun("use "), new InlineRun("git rebase", Code: true) },
            heading.Runs);
    }

    [Fact]
    public void TableHeaderAndBodyCellsAreInlineParsed()
    {
        var table = SingleBlock<TableBlock>("| **b** | plain |\n| --- | --- |\n| `c` | [t](u) |");
        Assert.Equal(new[] { new InlineRun("b", Bold: true) }, table.Header[0]);
        Assert.Equal(new[] { new InlineRun("plain") }, table.Header[1]);
        var row = Assert.Single(table.Rows);
        Assert.Equal(new[] { new InlineRun("c", Code: true) }, row[0]);
        Assert.Equal(new[] { new InlineRun("t", LinkUrl: "u") }, row[1]);
    }

    [Fact]
    public void ListItemParagraphIsInlineParsed()
    {
        var list = SingleBlock<ListBlock>("- **x** y");
        var item = Assert.Single(list.Items);
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(item.Blocks));
        Assert.Equal(
            new[] { new InlineRun("x", Bold: true), new InlineRun(" y") },
            para.Runs);
    }

    [Fact]
    public void TaskItemTextIsInlineParsedAfterMarkerStripping()
    {
        var list = SingleBlock<ListBlock>("- [x] ~~done~~");
        var item = Assert.Single(list.Items);
        Assert.True(item.TaskChecked);
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(item.Blocks));
        Assert.Equal(new[] { new InlineRun("done", Strikethrough: true) }, para.Runs);
    }

    [Fact]
    public void QuotedParagraphIsInlineParsed()
    {
        var quote = SingleBlock<QuoteBlock>("> see https://example.com.");
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks));
        Assert.Equal(
            new[]
            {
                new InlineRun("see "),
                new InlineRun("https://example.com", LinkUrl: "https://example.com"),
                new InlineRun("."),
            },
            para.Runs);
    }

    [Fact]
    public void CodeBlockTextStaysVerbatimAndUnparsed()
    {
        // Must hold before and after Step 2 wiring: fences never route through InlineParser.
        var code = SingleBlock<CodeBlock>("```\n**not bold** `no code span` [no](link)\n```");
        Assert.Equal("**not bold** `no code span` [no](link)", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void PlainTextBlocksStillHoldOneUnstyledRun()
    {
        // Markup-free content keeps the Step 1 shape: exactly one plain run.
        var doc = Parse("# title\n\njust words");
        var heading = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(new[] { new InlineRun("title") }, heading.Runs);
        var para = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Equal(new[] { new InlineRun("just words") }, para.Runs);
    }
}
