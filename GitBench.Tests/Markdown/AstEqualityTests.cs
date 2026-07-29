using GitBench.Features.Markdown.Parsing;
using Xunit;

namespace GitBench.Tests.Markdown;

/// <summary>
/// Structural-equality contract for the AST. Streaming keys block views by value equality, so two
/// separately built (or separately parsed) identical trees must compare equal — including every
/// list-typed property — and equal values must hash alike. Each builder call below allocates
/// fresh lists, so these tests fail under reference-based list comparison.
/// </summary>
public class AstEqualityTests
{
    private static InlineRun Run(string text) => new(text);

    private static IReadOnlyList<InlineRun> Runs(string text) => new[] { Run(text) };

    // Builds a document exercising every node type, with fresh list instances on every call.
    private static MarkdownDocument SampleDocument() => new(new MarkdownBlock[]
    {
        new HeadingBlock(2, Runs("title")),
        new ParagraphBlock(Runs("body text")),
        new CodeBlock("csharp", "var x = 1;", IsClosed: true),
        new ListBlock(Ordered: true, Start: 3, new[]
        {
            new ListItem(new MarkdownBlock[] { new ParagraphBlock(Runs("item")) }, TaskChecked: null),
            new ListItem(new MarkdownBlock[] { new ParagraphBlock(Runs("task")) }, TaskChecked: true),
        }),
        new QuoteBlock(new MarkdownBlock[]
        {
            new ParagraphBlock(Runs("quoted")),
            new QuoteBlock(new MarkdownBlock[] { new ParagraphBlock(Runs("nested")) }),
        }),
        new ThematicBreakBlock(),
        new TableBlock(
            new[] { ColumnAlignment.Left, ColumnAlignment.Right },
            new[] { Runs("h1"), Runs("h2") },
            new[] { new[] { Runs("a"), Runs("b") } }),
    });

    // ------------------------------------------------------- equal values compare equal

    [Fact]
    public void SeparatelyBuiltIdenticalDocumentsAreEqual()
    {
        Assert.Equal(SampleDocument(), SampleDocument());
    }

    [Fact]
    public void EqualDocumentsHashAlike()
    {
        Assert.Equal(SampleDocument().GetHashCode(), SampleDocument().GetHashCode());
    }

    [Fact]
    public void HeadingBlocksWithFreshRunListsAreEqual()
    {
        Assert.Equal(new HeadingBlock(3, Runs("h")), new HeadingBlock(3, Runs("h")));
    }

    [Fact]
    public void ParagraphBlocksWithFreshRunListsAreEqual()
    {
        Assert.Equal(new ParagraphBlock(Runs("p")), new ParagraphBlock(Runs("p")));
    }

    [Fact]
    public void ListBlocksWithFreshItemListsAreEqual()
    {
        static ListBlock Build() => new(Ordered: false, Start: 1, new[]
        {
            new ListItem(new MarkdownBlock[] { new ParagraphBlock(Runs("a")) }, TaskChecked: false),
        });
        Assert.Equal(Build(), Build());
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());
    }

    [Fact]
    public void QuoteBlocksWithFreshChildListsAreEqual()
    {
        static QuoteBlock Build() => new(new MarkdownBlock[] { new ParagraphBlock(Runs("q")) });
        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void TableBlocksWithFreshCellListsAreEqual()
    {
        static TableBlock Build() => new(
            new[] { ColumnAlignment.Center },
            new[] { Runs("h") },
            new[] { new[] { Runs("c") } });
        Assert.Equal(Build(), Build());
        Assert.Equal(Build().GetHashCode(), Build().GetHashCode());
    }

    [Fact]
    public void ScalarOnlyRecordsAreEqual()
    {
        Assert.Equal(new CodeBlock("py", "x", IsClosed: false), new CodeBlock("py", "x", IsClosed: false));
        Assert.Equal(new ThematicBreakBlock(), new ThematicBreakBlock());
        Assert.Equal(new InlineRun("t", Bold: true), new InlineRun("t", Bold: true));
    }

    // ------------------------------------------------------------ unequal values differ

    [Fact]
    public void DifferentHeadingLevelsAreNotEqual()
    {
        Assert.NotEqual(new HeadingBlock(1, Runs("h")), new HeadingBlock(2, Runs("h")));
    }

    [Fact]
    public void DifferentRunTextIsNotEqual()
    {
        Assert.NotEqual(new ParagraphBlock(Runs("a")), new ParagraphBlock(Runs("b")));
    }

    [Fact]
    public void DifferentCodeClosedStateIsNotEqual()
    {
        Assert.NotEqual(new CodeBlock("py", "x", IsClosed: true), new CodeBlock("py", "x", IsClosed: false));
    }

    [Fact]
    public void DifferentTaskStateIsNotEqual()
    {
        var blocks = new MarkdownBlock[] { new ParagraphBlock(Runs("a")) };
        Assert.NotEqual(new ListItem(blocks, TaskChecked: null), new ListItem(blocks, TaskChecked: false));
    }

    [Fact]
    public void DifferentBlockCountIsNotEqual()
    {
        var one = new MarkdownDocument(new MarkdownBlock[] { new ParagraphBlock(Runs("a")) });
        var two = new MarkdownDocument(new MarkdownBlock[]
        {
            new ParagraphBlock(Runs("a")),
            new ThematicBreakBlock(),
        });
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void DifferentColumnAlignmentIsNotEqual()
    {
        TableBlock Build(ColumnAlignment alignment) => new(
            new[] { alignment },
            new[] { Runs("h") },
            Array.Empty<IReadOnlyList<IReadOnlyList<InlineRun>>>());
        Assert.NotEqual(Build(ColumnAlignment.Left), Build(ColumnAlignment.Right));
    }

    [Fact]
    public void DifferentRunStylingIsNotEqual()
    {
        Assert.NotEqual(new InlineRun("t"), new InlineRun("t", Bold: true));
        Assert.NotEqual(new InlineRun("t"), new InlineRun("t", LinkUrl: "https://example.com"));
    }

    // ----------------------------------------------------------- parse-twice invariants

    private const string Markdown =
        "# Title\n\npara\n\n- a\n- [x] b\n\n> q\n\n---\n\n| h |\n| --- |\n| c |\n\n```py\nopen";

    [Fact]
    public void ParsingTheSameTextTwiceYieldsEqualDocuments()
    {
        var parser = new BasicMarkdownParser();
        Assert.Equal(parser.Parse(Markdown), parser.Parse(Markdown));
    }

    [Fact]
    public void ParsingTheSameTextTwiceYieldsEqualHashCodes()
    {
        var parser = new BasicMarkdownParser();
        Assert.Equal(parser.Parse(Markdown).GetHashCode(), parser.Parse(Markdown).GetHashCode());
    }

    [Fact]
    public void ParsingDifferentTextYieldsUnequalDocuments()
    {
        var parser = new BasicMarkdownParser();
        Assert.NotEqual(parser.Parse("# a"), parser.Parse("# b"));
    }
}
