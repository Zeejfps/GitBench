using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;

namespace GitBench.Tests.Markdown;

// Pure tests for InlineRunBuilder — the AST-run → RichTextRun mapping every block goes through.
// The builder is strictly 1:1 (the inline parser already merged same-style neighbors), so these
// pin flag mapping, not segmentation: bold → FontWeight.Bold; italic → the true italic family
// (MarkdownFonts.ItalicFamily); bold-italic → italic family + Bold; code → IsCode + mono family
// (DiffOptions.MonoFontFamily) + the theme's chip text color; link → LinkUrl + Underline + the
// theme's link color; hard-break "\n" runs pass through as "\n" RichTextRuns with no decoration
// (Step 4's layout interprets them). Strikethrough is deliberately unpinned — RichTextRun has no
// strikethrough channel yet, so it degrades to plain text.
public class InlineRunBuilderTests
{
    private const float BodySize = 13f;
    private const uint BaseColor = 0xFF111111;

    // Sentinel theme slots, deliberately unlike any real palette so accidental constants fail.
    private static readonly MarkdownStyles Styles = new(
        Link: 0xFF3366FF,
        LinkHover: 0xFF5588FF,
        CodeChipText: 0xFFAA7744,
        CodeChipBackground: 0xFF2A2A3A,
        CodeBlockBackground: 0xFF10121A,
        CodeBlockBorder: 0xFF303240,
        CodeBlockText: 0xFFD0D0D0,
        QuoteBar: 0xFF446688,
        QuoteText: 0xFF8899AA,
        Rule: 0xFF555555);

    private static IReadOnlyList<RichTextRun> Build(params InlineRun[] runs) =>
        InlineRunBuilder.Build(runs, Styles, BodySize, BaseColor);

    private static RichTextRun Single(InlineRun run) => Assert.Single(Build(run));

    // ---------- shape: 1:1, order, text passthrough ----------

    [Fact]
    public void EmptyInputYieldsEmptyOutput()
    {
        Assert.Empty(Build());
    }

    [Fact]
    public void BuilderIsOneToOneAndOrderPreserving()
    {
        var output = Build(
            new InlineRun("plain "),
            new InlineRun("bold", Bold: true),
            new InlineRun("code", Code: true),
            new InlineRun("link", LinkUrl: "https://example.com"),
            new InlineRun("\n"),
            new InlineRun("italic", Italic: true));

        Assert.Equal(
            new[] { "plain ", "bold", "code", "link", "\n", "italic" },
            output.Select(r => r.Text));
    }

    // ---------- plain ----------

    [Fact]
    public void PlainRunGetsBaseColorSizeAndNoDecorations()
    {
        var run = Single(new InlineRun("hello"));

        Assert.Equal(BaseColor, run.Style.TextColor.Value);
        Assert.Equal(BodySize, run.Style.FontSize.Value);
        Assert.NotEqual(FontWeight.Bold, run.Style.FontWeight.Value);
        Assert.NotEqual(MarkdownFonts.ItalicFamily, run.Style.FontFamily.Value);
        Assert.NotEqual(DiffOptions.MonoFontFamily, run.Style.FontFamily.Value);
        Assert.False(run.IsCode);
        Assert.False(run.Underline);
        Assert.Null(run.LinkUrl);
    }

    // ---------- emphasis ----------

    [Fact]
    public void BoldRunGetsBoldWeightOnTheDefaultFamily()
    {
        var run = Single(new InlineRun("strong", Bold: true));

        Assert.Equal(FontWeight.Bold, run.Style.FontWeight.Value);
        Assert.NotEqual(MarkdownFonts.ItalicFamily, run.Style.FontFamily.Value);
        Assert.Equal(BaseColor, run.Style.TextColor.Value);
    }

    [Fact]
    public void ItalicRunSwapsToTheItalicFamilyWithoutBold()
    {
        var run = Single(new InlineRun("lean", Italic: true));

        Assert.Equal(MarkdownFonts.ItalicFamily, run.Style.FontFamily.Value);
        Assert.NotEqual(FontWeight.Bold, run.Style.FontWeight.Value);
        Assert.Equal(BaseColor, run.Style.TextColor.Value);
    }

    [Fact]
    public void BoldItalicUsesTheItalicFamilyPlusBoldWeight()
    {
        var run = Single(new InlineRun("both", Bold: true, Italic: true));

        Assert.Equal(MarkdownFonts.ItalicFamily, run.Style.FontFamily.Value);
        Assert.Equal(FontWeight.Bold, run.Style.FontWeight.Value);
    }

    [Fact]
    public void BoldBaseMakesPlainAndItalicRunsBold()
    {
        // Headings pass bold: true — every run of the block gains Bold, italic keeps its family.
        var output = InlineRunBuilder.Build(
            new[] { new InlineRun("plain "), new InlineRun("lean", Italic: true) },
            Styles, BodySize, BaseColor, bold: true);

        Assert.Equal(FontWeight.Bold, output[0].Style.FontWeight.Value);
        Assert.Equal(FontWeight.Bold, output[1].Style.FontWeight.Value);
        Assert.Equal(MarkdownFonts.ItalicFamily, output[1].Style.FontFamily.Value);
    }

    // ---------- inline code ----------

    [Fact]
    public void CodeRunIsChipFlaggedMonoInTheChipTextColor()
    {
        var run = Single(new InlineRun("x = 1", Code: true));

        Assert.True(run.IsCode);
        Assert.Equal(DiffOptions.MonoFontFamily, run.Style.FontFamily.Value);
        Assert.Equal(Styles.CodeChipText, run.Style.TextColor.Value);
        Assert.False(run.Underline);
        Assert.Null(run.LinkUrl);
    }

    [Fact]
    public void CodeRunKeepsTheBlockFontSize()
    {
        // Inline code inside a heading renders at the heading's size, not a hardcoded body size.
        var output = InlineRunBuilder.Build(
            new[] { new InlineRun("Head ") , new InlineRun("code", Code: true) },
            Styles, fontSize: 22f, textColor: BaseColor, bold: true);

        Assert.Equal(22f, output[1].Style.FontSize.Value);
    }

    // ---------- links ----------

    [Fact]
    public void LinkRunCarriesUrlUnderlineAndLinkColor()
    {
        var run = Single(new InlineRun("docs", LinkUrl: "https://example.com/docs"));

        Assert.Equal("https://example.com/docs", run.LinkUrl);
        Assert.True(run.Underline);
        Assert.Equal(Styles.Link, run.Style.TextColor.Value);
        Assert.False(run.IsCode);
    }

    [Fact]
    public void BoldLinkKeepsBoldWeightAndLinkStyling()
    {
        var run = Single(new InlineRun("docs", Bold: true, LinkUrl: "https://example.com"));

        Assert.Equal(FontWeight.Bold, run.Style.FontWeight.Value);
        Assert.True(run.Underline);
        Assert.Equal("https://example.com", run.LinkUrl);
        Assert.Equal(Styles.Link, run.Style.TextColor.Value);
    }

    // ---------- hard breaks ----------

    [Fact]
    public void HardBreakPassesThroughAsAnUndecoratedNewlineRun()
    {
        var run = Single(new InlineRun("\n"));

        Assert.Equal("\n", run.Text);
        Assert.False(run.IsCode);
        Assert.False(run.Underline);
        Assert.Null(run.LinkUrl);
    }

    [Fact]
    public void HardBreakStaysUndecoratedUnderABoldBase()
    {
        // Even inside a heading the "\n" run carries no chip/underline/link decoration.
        var output = InlineRunBuilder.Build(
            new[] { new InlineRun("a"), new InlineRun("\n"), new InlineRun("b") },
            Styles, BodySize, BaseColor, bold: true);

        Assert.Equal("\n", output[1].Text);
        Assert.False(output[1].IsCode);
        Assert.False(output[1].Underline);
        Assert.Null(output[1].LinkUrl);
    }

    // ---------- sizing ----------

    [Fact]
    public void FontSizeAppliesToEveryRun()
    {
        var output = InlineRunBuilder.Build(
            new[]
            {
                new InlineRun("a"),
                new InlineRun("b", Bold: true),
                new InlineRun("c", Italic: true),
                new InlineRun("d", LinkUrl: "https://example.com"),
            },
            Styles, fontSize: 16f, textColor: BaseColor);

        Assert.All(output, r => Assert.Equal(16f, r.Style.FontSize.Value));
    }
}
