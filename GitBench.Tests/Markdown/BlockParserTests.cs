using GitBench.Features.Markdown.Parsing;
using Xunit;

namespace GitBench.Tests.Markdown;

/// <summary>
/// Block-grammar contract for <see cref="BasicMarkdownParser"/>. The inputs here are markup-free,
/// so every paragraph, heading, and table cell must hold exactly one unstyled
/// <see cref="InlineRun"/> (<see cref="RawText"/> asserts that shape). Consecutive paragraph
/// lines join with "\n"; an empty table cell is an empty run list.
/// </summary>
public class BlockParserTests
{
    private static MarkdownDocument Parse(string markdown) => new BasicMarkdownParser().Parse(markdown);

    private static T SingleBlock<T>(string markdown) where T : MarkdownBlock
    {
        var doc = Parse(markdown);
        var block = Assert.Single(doc.Blocks);
        return Assert.IsType<T>(block);
    }

    // Markup-free text yields one unstyled run; assert exactly that and return it.
    private static string RawText(IReadOnlyList<InlineRun> runs)
    {
        var run = Assert.Single(runs);
        Assert.False(run.Bold);
        Assert.False(run.Italic);
        Assert.False(run.Code);
        Assert.False(run.Strikethrough);
        Assert.Null(run.LinkUrl);
        return run.Text;
    }

    private static string ItemText(ListItem item)
    {
        var para = Assert.IsType<ParagraphBlock>(item.Blocks[0]);
        return RawText(para.Runs);
    }

    // Empty cells (explicit or padding for a short row) are an empty run list.
    private static string CellText(IReadOnlyList<InlineRun> cell)
        => cell.Count == 0 ? string.Empty : RawText(cell);

    // ---------------------------------------------------------------- headings

    [Theory]
    [InlineData("# one", 1, "one")]
    [InlineData("## two", 2, "two")]
    [InlineData("### three", 3, "three")]
    [InlineData("#### four", 4, "four")]
    [InlineData("##### five", 5, "five")]
    [InlineData("###### six", 6, "six")]
    public void AtxHeadingLevelsOneThroughSix(string markdown, int level, string text)
    {
        var heading = SingleBlock<HeadingBlock>(markdown);
        Assert.Equal(level, heading.Level);
        Assert.Equal(text, RawText(heading.Runs));
    }

    [Theory]
    [InlineData("####### seven")]
    [InlineData("############ many")]
    public void SevenOrMoreHashesIsParagraph(string markdown)
    {
        var para = SingleBlock<ParagraphBlock>(markdown);
        Assert.Equal(markdown, RawText(para.Runs));
    }

    [Theory]
    [InlineData("#nospace")]
    [InlineData("##nospace")]
    public void MissingSpaceAfterHashIsParagraph(string markdown)
    {
        var para = SingleBlock<ParagraphBlock>(markdown);
        Assert.Equal(markdown, RawText(para.Runs));
    }

    [Theory]
    [InlineData("# title ##", 1, "title")]
    [InlineData("### title ###", 3, "title")]
    [InlineData("###### t #", 6, "t")]
    public void TrailingHashesAreStripped(string markdown, int level, string text)
    {
        var heading = SingleBlock<HeadingBlock>(markdown);
        Assert.Equal(level, heading.Level);
        Assert.Equal(text, RawText(heading.Runs));
    }

    [Fact]
    public void TrailingHashesWithoutPrecedingSpaceAreKept()
    {
        var heading = SingleBlock<HeadingBlock>("# title#");
        Assert.Equal("title#", RawText(heading.Runs));
    }

    [Fact]
    public void HeadingTextIsTrimmed()
    {
        var heading = SingleBlock<HeadingBlock>("##   spaced heading   ");
        Assert.Equal(2, heading.Level);
        Assert.Equal("spaced heading", RawText(heading.Runs));
    }

    [Fact]
    public void HeadingInterruptsParagraph()
    {
        var doc = Parse("text\n# h");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("text", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs));
        Assert.Equal("h", RawText(Assert.IsType<HeadingBlock>(doc.Blocks[1]).Runs));
    }

    // -------------------------------------------------------------- paragraphs

    [Fact]
    public void ConsecutiveLinesMergeIntoOneParagraph()
    {
        var para = SingleBlock<ParagraphBlock>("line one\nline two\nline three");
        Assert.Equal("line one\nline two\nline three", RawText(para.Runs));
    }

    [Fact]
    public void BlankLineSeparatesParagraphs()
    {
        var doc = Parse("first\n\nsecond");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("first", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs));
        Assert.Equal("second", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
    }

    [Fact]
    public void MultipleBlankLinesStillSeparateTwoParagraphs()
    {
        var doc = Parse("first\n\n\n\nsecond");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.All(doc.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void TrailingSpacesBecomeHardBreakRun()
    {
        // Trailing double spaces survive the line join and reach the inline parser, which turns
        // them into the dedicated unstyled "\n" hard-break run.
        var para = SingleBlock<ParagraphBlock>("a  \nb");
        Assert.Equal(
            new[] { new InlineRun("a"), new InlineRun("\n"), new InlineRun("b") },
            para.Runs);
    }

    [Fact]
    public void LeadingWhitespaceOnParagraphLinesIsStripped()
    {
        // Indented code blocks are out of scope, so indentation is not significant here.
        var para = SingleBlock<ParagraphBlock>("  lead\n    indented");
        Assert.Equal("lead\nindented", RawText(para.Runs));
    }

    [Fact]
    public void EmptyInputYieldsEmptyDocument()
    {
        Assert.Empty(Parse("").Blocks);
    }

    [Fact]
    public void WhitespaceOnlyInputYieldsEmptyDocument()
    {
        Assert.Empty(Parse(" \n\t\n\n").Blocks);
    }

    [Fact]
    public void CrlfLineEndingsParseLikeLf()
    {
        var doc = Parse("# h\r\n\r\npara\r\nmore");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("h", RawText(Assert.IsType<HeadingBlock>(doc.Blocks[0]).Runs));
        Assert.Equal("para\nmore", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
    }

    // -------------------------------------------------------------- code fences

    [Fact]
    public void BacktickFenceProducesClosedCodeBlock()
    {
        var code = SingleBlock<CodeBlock>("```\nvar x = 1;\n```");
        Assert.Null(code.Language);
        Assert.Equal("var x = 1;", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void TildeFenceProducesClosedCodeBlock()
    {
        var code = SingleBlock<CodeBlock>("~~~\ncontent\n~~~");
        Assert.Null(code.Language);
        Assert.Equal("content", code.Text);
        Assert.True(code.IsClosed);
    }

    [Theory]
    [InlineData("```csharp\nvar x;\n```", "csharp")]
    [InlineData("``` js startline=3 \nx\n```", "js")]
    [InlineData("~~~python\nx\n~~~", "python")]
    public void InfoStringFirstWordBecomesLanguage(string markdown, string language)
    {
        var code = SingleBlock<CodeBlock>(markdown);
        Assert.Equal(language, code.Language);
    }

    [Fact]
    public void ContentIsVerbatimAndNeverInlineParsed()
    {
        var code = SingleBlock<CodeBlock>("```\n**bold** | pipe\n# not a heading\n\n  indented\n```");
        Assert.Equal("**bold** | pipe\n# not a heading\n\n  indented", code.Text);
    }

    [Fact]
    public void UnterminatedFenceAtEofIsOpenCodeBlock()
    {
        var code = SingleBlock<CodeBlock>("```py\nprint(1)");
        Assert.Equal("py", code.Language);
        Assert.Equal("print(1)", code.Text);
        Assert.False(code.IsClosed);
    }

    [Fact]
    public void UnterminatedFenceWithTrailingNewlineDoesNotGainEmptyLine()
    {
        var code = SingleBlock<CodeBlock>("```\nx\n");
        Assert.Equal("x", code.Text);
        Assert.False(code.IsClosed);
    }

    [Fact]
    public void BareOpeningFenceIsEmptyOpenCodeBlock()
    {
        var code = SingleBlock<CodeBlock>("```");
        Assert.Equal("", code.Text);
        Assert.False(code.IsClosed);
    }

    [Fact]
    public void TildeLineInsideBacktickFenceIsContent()
    {
        var code = SingleBlock<CodeBlock>("```\n~~~\n```");
        Assert.Equal("~~~", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void BacktickLineInsideTildeFenceIsContent()
    {
        var code = SingleBlock<CodeBlock>("~~~\n```\n~~~");
        Assert.Equal("```", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void ShorterFenceRunInsideLongerFenceIsContent()
    {
        var code = SingleBlock<CodeBlock>("````\n```\n````");
        Assert.Equal("```", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void LongerClosingFenceCloses()
    {
        var code = SingleBlock<CodeBlock>("```\nx\n`````");
        Assert.Equal("x", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void FenceInterruptsParagraph()
    {
        var doc = Parse("text\n```\nc\n```");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Equal("c", Assert.IsType<CodeBlock>(doc.Blocks[1]).Text);
    }

    // ------------------------------------------------------------------- lists

    [Theory]
    [InlineData("- a\n- b")]
    [InlineData("* a\n* b")]
    [InlineData("+ a\n+ b")]
    public void UnorderedMarkersProduceOneList(string markdown)
    {
        var list = SingleBlock<ListBlock>(markdown);
        Assert.False(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("a", ItemText(list.Items[0]));
        Assert.Equal("b", ItemText(list.Items[1]));
    }

    [Fact]
    public void PlainItemHasNullTaskStateAndSingleParagraph()
    {
        var list = SingleBlock<ListBlock>("- only");
        var item = Assert.Single(list.Items);
        Assert.Null(item.TaskChecked);
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(item.Blocks));
        Assert.Equal("only", RawText(para.Runs));
    }

    [Theory]
    [InlineData("1. a\n2. b", 1)]
    [InlineData("3. a\n4. b", 3)]
    [InlineData("1) a\n2) b", 1)]
    [InlineData("7) x", 7)]
    public void OrderedListsHonorStart(string markdown, int start)
    {
        var list = SingleBlock<ListBlock>(markdown);
        Assert.True(list.Ordered);
        Assert.Equal(start, list.Start);
    }

    [Fact]
    public void SubsequentOrderedNumbersDoNotSplitTheList()
    {
        var list = SingleBlock<ListBlock>("1. a\n7. b\n3. c");
        Assert.True(list.Ordered);
        Assert.Equal(1, list.Start);
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void TaskItemsMapToTaskChecked()
    {
        var list = SingleBlock<ListBlock>("- [ ] open\n- [x] done\n- plain");
        Assert.Equal(3, list.Items.Count);
        Assert.False(list.Items[0].TaskChecked);
        Assert.Equal("open", ItemText(list.Items[0]));
        Assert.True(list.Items[1].TaskChecked);
        Assert.Equal("done", ItemText(list.Items[1]));
        Assert.Null(list.Items[2].TaskChecked);
        Assert.Equal("plain", ItemText(list.Items[2]));
    }

    [Fact]
    public void UppercaseXIsChecked()
    {
        var list = SingleBlock<ListBlock>("- [X] done");
        Assert.True(Assert.Single(list.Items).TaskChecked);
    }

    [Fact]
    public void NonTaskBracketsStayLiteralItemText()
    {
        var list = SingleBlock<ListBlock>("- [y] nope");
        var item = Assert.Single(list.Items);
        Assert.Null(item.TaskChecked);
        Assert.Equal("[y] nope", ItemText(item));
    }

    [Fact]
    public void NestedListViaIndentation()
    {
        var list = SingleBlock<ListBlock>("- a\n  - b\n- c");
        Assert.Equal(2, list.Items.Count);

        var first = list.Items[0];
        Assert.Equal(2, first.Blocks.Count);
        Assert.Equal("a", RawText(Assert.IsType<ParagraphBlock>(first.Blocks[0]).Runs));
        var nested = Assert.IsType<ListBlock>(first.Blocks[1]);
        Assert.False(nested.Ordered);
        Assert.Equal("b", ItemText(Assert.Single(nested.Items)));

        Assert.Equal("c", ItemText(list.Items[1]));
    }

    [Fact]
    public void OrderedListNestsInsideUnorderedItem()
    {
        var list = SingleBlock<ListBlock>("- a\n  1. one\n  2. two");
        var item = Assert.Single(list.Items);
        var nested = Assert.IsType<ListBlock>(item.Blocks[1]);
        Assert.True(nested.Ordered);
        Assert.Equal(1, nested.Start);
        Assert.Equal(2, nested.Items.Count);
    }

    [Fact]
    public void IndentedContinuationLineMergesIntoItemParagraph()
    {
        var list = SingleBlock<ListBlock>("- first\n  second\n- next");
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("first\nsecond", ItemText(list.Items[0]));
        Assert.Equal("next", ItemText(list.Items[1]));
    }

    [Fact]
    public void BlankLineThenUnindentedTextEndsTheList()
    {
        var doc = Parse("- a\n\npara");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.IsType<ListBlock>(doc.Blocks[0]);
        Assert.Equal("para", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
    }

    [Fact]
    public void ListInterruptsParagraph()
    {
        var doc = Parse("text\n- item");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("text", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs));
        var list = Assert.IsType<ListBlock>(doc.Blocks[1]);
        Assert.Equal("item", ItemText(Assert.Single(list.Items)));
    }

    // ------------------------------------------------------------- blockquotes

    [Fact]
    public void SimpleQuoteWrapsParagraph()
    {
        var quote = SingleBlock<QuoteBlock>("> quoted");
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks));
        Assert.Equal("quoted", RawText(para.Runs));
    }

    [Fact]
    public void ConsecutiveQuotedLinesMergeIntoOneParagraph()
    {
        var quote = SingleBlock<QuoteBlock>("> a\n> b");
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks));
        Assert.Equal("a\nb", RawText(para.Runs));
    }

    [Fact]
    public void SpaceAfterQuoteMarkerIsOptional()
    {
        var quote = SingleBlock<QuoteBlock>(">terse");
        Assert.Equal("terse", RawText(Assert.IsType<ParagraphBlock>(quote.Blocks[0]).Runs));
    }

    [Fact]
    public void QuotesNest()
    {
        var outer = SingleBlock<QuoteBlock>("> > deep");
        var inner = Assert.IsType<QuoteBlock>(Assert.Single(outer.Blocks));
        Assert.Equal("deep", RawText(Assert.IsType<ParagraphBlock>(Assert.Single(inner.Blocks)).Runs));
    }

    [Fact]
    public void QuoteContainsHeadingAndParagraph()
    {
        var quote = SingleBlock<QuoteBlock>("> # h\n> p");
        Assert.Equal(2, quote.Blocks.Count);
        var heading = Assert.IsType<HeadingBlock>(quote.Blocks[0]);
        Assert.Equal(1, heading.Level);
        Assert.Equal("h", RawText(heading.Runs));
        Assert.Equal("p", RawText(Assert.IsType<ParagraphBlock>(quote.Blocks[1]).Runs));
    }

    [Fact]
    public void QuoteContainsList()
    {
        var quote = SingleBlock<QuoteBlock>("> - a\n> - b");
        var list = Assert.IsType<ListBlock>(Assert.Single(quote.Blocks));
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void QuoteContainsCodeBlock()
    {
        var quote = SingleBlock<QuoteBlock>("> ```\n> x\n> ```");
        var code = Assert.IsType<CodeBlock>(Assert.Single(quote.Blocks));
        Assert.Equal("x", code.Text);
        Assert.True(code.IsClosed);
    }

    [Fact]
    public void BlankQuotedLineSeparatesParagraphsInsideQuote()
    {
        var quote = SingleBlock<QuoteBlock>("> a\n>\n> b");
        Assert.Equal(2, quote.Blocks.Count);
        Assert.Equal("a", RawText(Assert.IsType<ParagraphBlock>(quote.Blocks[0]).Runs));
        Assert.Equal("b", RawText(Assert.IsType<ParagraphBlock>(quote.Blocks[1]).Runs));
    }

    [Fact]
    public void UnprefixedLineEndsTheQuoteNoLazyContinuation()
    {
        // Subset rule: every quoted line carries the marker; no CommonMark lazy continuation.
        var doc = Parse("> a\nb");
        Assert.Equal(2, doc.Blocks.Count);
        var quote = Assert.IsType<QuoteBlock>(doc.Blocks[0]);
        Assert.Equal("a", RawText(Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks)).Runs));
        Assert.Equal("b", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
    }

    [Fact]
    public void BlankLineSeparatesTwoQuotes()
    {
        var doc = Parse("> a\n\n> b");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.All(doc.Blocks, b => Assert.IsType<QuoteBlock>(b));
    }

    // --------------------------------------------------------- thematic breaks

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("----------")]
    [InlineData("- - -")]
    [InlineData("* * *")]
    [InlineData("_ _ _")]
    [InlineData("---   ")]
    public void ThematicBreakForms(string markdown)
    {
        SingleBlock<ThematicBreakBlock>(markdown);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("**")]
    [InlineData("__")]
    [InlineData("-*-")]
    [InlineData("===")]
    public void TooShortOrMixedRunsAreParagraphs(string markdown)
    {
        var para = SingleBlock<ParagraphBlock>(markdown);
        Assert.Equal(markdown, RawText(para.Runs));
    }

    [Fact]
    public void DashesAfterParagraphAreBreakNotSetextHeading()
    {
        // Setext headings are out of scope: "---" always matches as a thematic break.
        var doc = Parse("text\n---");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("text", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs));
        Assert.IsType<ThematicBreakBlock>(doc.Blocks[1]);
        Assert.DoesNotContain(doc.Blocks, b => b is HeadingBlock);
    }

    [Fact]
    public void BreakSeparatesParagraphs()
    {
        var doc = Parse("a\n\n---\n\nb");
        Assert.Equal(3, doc.Blocks.Count);
        Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.IsType<ThematicBreakBlock>(doc.Blocks[1]);
        Assert.IsType<ParagraphBlock>(doc.Blocks[2]);
    }

    // ------------------------------------------------------------------ tables

    [Fact]
    public void BasicPipeTable()
    {
        var table = SingleBlock<TableBlock>("| a | b |\n| --- | --- |\n| 1 | 2 |");
        Assert.Equal(new[] { ColumnAlignment.None, ColumnAlignment.None }, table.Columns);
        Assert.Equal(2, table.Header.Count);
        Assert.Equal("a", CellText(table.Header[0]));
        Assert.Equal("b", CellText(table.Header[1]));
        var row = Assert.Single(table.Rows);
        Assert.Equal("1", CellText(row[0]));
        Assert.Equal("2", CellText(row[1]));
    }

    [Fact]
    public void DelimiterRowSetsColumnAlignments()
    {
        var table = SingleBlock<TableBlock>("| a | b | c | d |\n| --- | :-- | :-: | --: |\n| 1 | 2 | 3 | 4 |");
        Assert.Equal(
            new[] { ColumnAlignment.None, ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right },
            table.Columns);
    }

    [Fact]
    public void HeaderPlusDelimiterWithoutDataRowsIsStillATable()
    {
        var table = SingleBlock<TableBlock>("| a | b |\n| --- | --- |");
        Assert.Equal(2, table.Header.Count);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void HeaderWithoutDelimiterStaysParagraph()
    {
        var doc = Parse("| a | b |\n| 1 | 2 |");
        Assert.DoesNotContain(doc.Blocks, b => b is TableBlock);
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("| a | b |\n| 1 | 2 |", RawText(para.Runs));
    }

    [Fact]
    public void OuterPipesAreOptional()
    {
        var table = SingleBlock<TableBlock>("a | b\n--- | ---\n1 | 2");
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("a", CellText(table.Header[0]));
        Assert.Equal("2", CellText(Assert.Single(table.Rows)[1]));
    }

    [Fact]
    public void ShortRowIsPaddedWithEmptyCells()
    {
        var table = SingleBlock<TableBlock>("| a | b |\n| --- | --- |\n| 1 |");
        var row = Assert.Single(table.Rows);
        Assert.Equal(2, row.Count);
        Assert.Equal("1", CellText(row[0]));
        Assert.Empty(row[1]); // padded cell is an empty run list
    }

    [Fact]
    public void LongRowExtraCellsAreDropped()
    {
        var table = SingleBlock<TableBlock>("| a | b |\n| --- | --- |\n| 1 | 2 | 3 |");
        var row = Assert.Single(table.Rows);
        Assert.Equal(2, row.Count);
        Assert.Equal("2", CellText(row[1]));
    }

    [Fact]
    public void EscapedPipeDoesNotSplitCells()
    {
        var table = SingleBlock<TableBlock>("| a \\| b | c |\n| --- | --- |");
        Assert.Equal(2, table.Header.Count);
        Assert.Equal("a | b", CellText(table.Header[0]));
        Assert.Equal("c", CellText(table.Header[1]));
    }

    [Fact]
    public void CellWhitespaceIsTrimmed()
    {
        var table = SingleBlock<TableBlock>("|  a  |  b  |\n| --- | --- |\n|  1  |  2  |");
        Assert.Equal("a", CellText(table.Header[0]));
        Assert.Equal("1", CellText(Assert.Single(table.Rows)[0]));
    }

    [Fact]
    public void ExplicitlyEmptyCellIsEmptyRunList()
    {
        var table = SingleBlock<TableBlock>("| a |  |\n| --- | --- |\n|  | b |");
        Assert.Empty(table.Header[1]);
        var row = Assert.Single(table.Rows);
        Assert.Empty(row[0]);
        Assert.Equal("b", CellText(row[1]));
    }

    [Fact]
    public void DelimiterColumnCountMismatchStaysParagraph()
    {
        var doc = Parse("| a | b |\n| --- |");
        Assert.DoesNotContain(doc.Blocks, b => b is TableBlock);
        Assert.All(doc.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void MalformedDelimiterCellStaysParagraph()
    {
        var doc = Parse("| a | b |\n| --- | xx |");
        Assert.DoesNotContain(doc.Blocks, b => b is TableBlock);
        Assert.All(doc.Blocks, b => Assert.IsType<ParagraphBlock>(b));
    }

    [Fact]
    public void LineWithoutPipeEndsTheTable()
    {
        var doc = Parse("| a |\n| --- |\n| 1 |\nplain");
        Assert.Equal(2, doc.Blocks.Count);
        var table = Assert.IsType<TableBlock>(doc.Blocks[0]);
        Assert.Single(table.Rows);
        Assert.Equal("plain", RawText(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
    }

    // ------------------------------------------------------------- degradation

    [Fact]
    public void RawHtmlStaysLiteralParagraphText()
    {
        var para = SingleBlock<ParagraphBlock>("<div>\nhello\n</div>");
        Assert.Equal("<div>\nhello\n</div>", RawText(para.Runs));
    }

    [Fact]
    public void SelfClosingHtmlLineIsLiteralParagraph()
    {
        var para = SingleBlock<ParagraphBlock>("<br/>");
        Assert.Equal("<br/>", RawText(para.Runs));
    }

    [Fact]
    public void SetextLookingEqualsInputDoesNotBecomeHeading()
    {
        // Setext is out of scope; "===" is not a thematic break either, so the two lines merge
        // into one paragraph.
        var doc = Parse("text\n===");
        Assert.DoesNotContain(doc.Blocks, b => b is HeadingBlock);
        var para = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Equal("text\n===", RawText(para.Runs));
    }

    // -------------------------------------------------------------- robustness

    // Exercises every construct, including an unterminated fence at the very end. Kept as one
    // string so the prefix theory below can slice it at every char boundary.
    private const string Fixture =
        "# Heading one\n" +
        "\n" +
        "Paragraph with **bold**, *italic*, `code`, ~~strike~~, and [link](https://example.com).\n" +
        "Second line of the same paragraph.\n" +
        "\n" +
        "```csharp\n" +
        "var s = \"fence | pipe\";\n" +
        "```\n" +
        "\n" +
        "~~~text\n" +
        "tilde fence\n" +
        "~~~\n" +
        "\n" +
        "- unordered\n" +
        "- [ ] open task\n" +
        "- [x] done task\n" +
        "  - nested\n" +
        "    continued\n" +
        "\n" +
        "1. ordered one\n" +
        "3) other delimiter\n" +
        "\n" +
        "> quoted line\n" +
        "> continues\n" +
        ">\n" +
        "> > nested quote\n" +
        "> - quoted item\n" +
        "\n" +
        "---\n" +
        "\n" +
        "| Col A | Col B | Col C |\n" +
        "| :---- | :---: | ----: |\n" +
        "| a \\| pipe | b | c |\n" +
        "| short |\n" +
        "\n" +
        "***\n" +
        "\n" +
        "<div>raw html</div>\n" +
        "\n" +
        "####### not a heading\n" +
        "#nospace\n" +
        "\n" +
        "setext bait\n" +
        "===\n" +
        "\n" +
        "```py\n" +
        "unterminated fence";

    public static TheoryData<int> FixturePrefixLengths()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i <= Fixture.Length; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(FixturePrefixLengths))]
    public void ParseNeverThrowsOnAnyPrefixOfTheFixture(int length)
    {
        // The streaming guarantee: every partial document parses without throwing.
        var doc = Parse(Fixture[..length]);
        Assert.NotNull(doc);
        Assert.NotNull(doc.Blocks);
    }

    [Fact]
    public void FullFixtureContainsEveryBlockKind()
    {
        var doc = Parse(Fixture);
        Assert.Contains(doc.Blocks, b => b is HeadingBlock);
        Assert.Contains(doc.Blocks, b => b is ParagraphBlock);
        Assert.Contains(doc.Blocks, b => b is CodeBlock { IsClosed: true });
        Assert.Contains(doc.Blocks, b => b is CodeBlock { IsClosed: false });
        Assert.Contains(doc.Blocks, b => b is ListBlock { Ordered: false });
        Assert.Contains(doc.Blocks, b => b is ListBlock { Ordered: true });
        Assert.Contains(doc.Blocks, b => b is QuoteBlock);
        Assert.Contains(doc.Blocks, b => b is ThematicBreakBlock);
        Assert.Contains(doc.Blocks, b => b is TableBlock);
    }
}
