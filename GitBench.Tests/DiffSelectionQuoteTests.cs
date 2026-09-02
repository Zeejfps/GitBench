using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// What a diff selection carries when it becomes a question: the code, the file, the lines, and
/// which side of the change it came off.
/// </summary>
public sealed class DiffSelectionQuoteTests
{
    // A small hunk: two context lines, a removal, an addition, one more context line.
    private static readonly IReadOnlyList<DiffRow> Rows =
    [
        new DiffRow.HunkSeparator("@@ -40,4 +40,4 @@", null),
        new DiffRow.Line(DiffLineKind.Context, Gutter(40), Gutter(40), DiffLineText.Of("public void Run()")),
        new DiffRow.Line(DiffLineKind.Context, Gutter(41), Gutter(41), DiffLineText.Of("{")),
        new DiffRow.Line(DiffLineKind.Removed, Gutter(42), DiffGutterNumber.None, DiffLineText.Of("    Legacy();")),
        new DiffRow.Line(DiffLineKind.Added, DiffGutterNumber.None, Gutter(42), DiffLineText.Of("    Modern();")),
        new DiffRow.Line(DiffLineKind.Context, Gutter(43), Gutter(43), DiffLineText.Of("}")),
    ];

    // Two hunks of one file with the bar between them: the bar stands for no line, so a drag
    // across it has to take its range off the code either side.
    private static readonly IReadOnlyList<DiffRow> AcrossABar =
    [
        new DiffRow.Line(DiffLineKind.Context, Gutter(10), Gutter(10), DiffLineText.Of("First();")),
        new DiffRow.HunkSeparator("@@ -80,1 +80,1 @@", null),
        new DiffRow.Line(DiffLineKind.Added, DiffGutterNumber.None, Gutter(80), DiffLineText.Of("Second();")),
    ];

    [Fact]
    public void ASelectionDraggedAcrossAHunkBarRangesFromTheCodeEitherSideOfIt()
    {
        var quote = DiffSelectionQuote.Build(
            AcrossABar,
            new DiffTextPos(default, default),
            new DiffTextPos(new RowIndex(2), new ExpandedColumn(9)),
            "src/Runner.cs")!;

        Assert.Equal(Line(10), quote.StartLine);
        Assert.Equal(Line(80), quote.EndLine);
        Assert.Equal("First();\nSecond();", quote.Text);
        Assert.Contains("lines 10-80", quote.ToPrompt(null), StringComparison.Ordinal);
    }

    private static DiffSelectionQuote Quote(int fromRow, int toRow, string path = "src/Runner.cs") =>
        DiffSelectionQuote.Build(
            Rows,
            new DiffTextPos(new RowIndex(fromRow), default),
            new DiffTextPos(new RowIndex(toRow), Rows[toRow] is DiffRow.Line line ? line.Text.End : default),
            path)!;

    // A line number tells the model where to look; the declaration tells it what it is looking at.
    [Fact]
    public void AnAddedSelectionNamesTheDeclarationItSitsIn()
    {
        var quote = QuoteWith(4, 4, Annotations());

        Assert.Equal("Runner.Run()", quote.Declaration);
        Assert.Contains("in `Runner.Run()`", quote.ToPrompt(null), StringComparison.Ordinal);
    }

    // A selection of only removed lines exists in the before-side file and nowhere else, so the
    // before-side outline is the one that can name it — the rule a hunk header already follows.
    [Fact]
    public void ARemovedSelectionIsNamedByTheBeforeSideOutline()
    {
        var quote = QuoteWith(3, 3, Annotations());

        Assert.Equal("Runner.Legacy()", quote.Declaration);
    }

    [Fact]
    public void WithNoOutlineTheQuoteNamesNoDeclarationAndSaysNothingExtra()
    {
        var quote = Quote(4, 4);

        Assert.Null(quote.Declaration);
        Assert.DoesNotContain(" in `", quote.ToPrompt(null), StringComparison.Ordinal);
    }

    private static DiffAnnotations Annotations() => new(
        null,
        new FileOutline([Node("Runner", 38, 50, [Node("Run()", 40, 43)])]),
        new FileOutline([Node("Runner", 38, 50, [Node("Legacy()", 40, 43)])]));

    private static DiffGutterNumber Gutter(int line) => DiffGutterNumber.Of(new FileLine(line));

    private static FileLine? Line(int line) => new FileLine(line);

    private static OutlineNode Node(string name, int start, int end, IReadOnlyList<OutlineNode>? children = null)
    {
        var open = name.EndsWith("()", StringComparison.Ordinal);
        return new OutlineNode(
            open ? name[..^2] : name,
            open ? SymbolKind.Method : SymbolKind.Class,
            open ? string.Empty : null,
            start,
            end,
            SignatureEndLine: start,
            children ?? []);
    }

    private static DiffSelectionQuote QuoteWith(int fromRow, int toRow, DiffAnnotations annotations) =>
        DiffSelectionQuote.Build(
            Rows,
            new DiffTextPos(new RowIndex(fromRow), default),
            new DiffTextPos(new RowIndex(toRow), Rows[toRow] is DiffRow.Line line ? line.Text.End : default),
            "src/Runner.cs",
            annotations)!;

    [Fact]
    public void AnAddedSelection_CarriesThePathTheLineAndTheSide()
    {
        var quote = Quote(4, 4);

        Assert.Equal("src/Runner.cs", quote.Path);
        Assert.Equal(Line(42), quote.StartLine);
        Assert.Equal(Line(42), quote.EndLine);
        Assert.Equal(DiffQuoteSide.Added, quote.Side);
        Assert.Equal("    Modern();", quote.Text);
    }

    // A question about a removed line means something different from one about an added line, so the
    // side is not a nicety — it is half the question.
    [Fact]
    public void ARemovedSelection_IsNotReportedAsAdded()
    {
        var quote = Quote(3, 3);

        Assert.Equal(DiffQuoteSide.Removed, quote.Side);
        Assert.Equal("    Legacy();", quote.Text);
        // A removed line has no after-side number, so the before-side one stands in rather than
        // leaving the range blank.
        Assert.Equal(Line(42), quote.StartLine);
    }

    [Fact]
    public void AContextOnlySelection_SaysSo()
    {
        Assert.Equal(DiffQuoteSide.Context, Quote(1, 2).Side);
    }

    [Fact]
    public void ASelectionSpanningBothSides_IsMixedAndKeepsItsRange()
    {
        var quote = Quote(1, 5);

        Assert.Equal(DiffQuoteSide.Mixed, quote.Side);
        Assert.Equal(Line(40), quote.StartLine);
        Assert.Equal(Line(43), quote.EndLine);
    }

    // The clipboard's own extractor, not a second one: the text handed to the model is exactly what
    // Ctrl+C would have produced — no gutters, no +/- markers, and the "@@" bar dropped.
    [Fact]
    public void TheText_IsWhatTheCopyPipelineProduces()
    {
        var start = new DiffTextPos(default, default);
        var end = new DiffTextPos(new RowIndex(5), new ExpandedColumn(1));

        var quote = DiffSelectionQuote.Build(Rows, start, end, "src/Runner.cs")!;

        Assert.Equal(DiffSelectionModel.BuildCopyText(Rows, start, end), quote.Text);
        Assert.DoesNotContain("@@", quote.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("+", quote.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectionOverNoCodeLines_IsNoQuestion()
    {
        Assert.Null(DiffSelectionQuote.Build(
            Rows, new DiffTextPos(default, default), new DiffTextPos(default, default), "x.cs"));
    }

    [Fact]
    public void ThePrompt_NamesThePathTheRangeAndTheSideAroundTheFencedCode()
    {
        var prompt = Quote(3, 3).ToPrompt("What could break here?");

        Assert.StartsWith("What could break here?", prompt, StringComparison.Ordinal);
        Assert.Contains("`src/Runner.cs`", prompt, StringComparison.Ordinal);
        Assert.Contains("line 42", prompt, StringComparison.Ordinal);
        Assert.Contains("removed lines", prompt, StringComparison.Ordinal);
        Assert.Contains("```\n    Legacy();\n```", prompt, StringComparison.Ordinal);
    }

    // The free-form case leads with the quote and nothing else: the question is still the person's
    // to write, underneath it.
    [Fact]
    public void ThePrompt_LeadsWithTheQuoteWhenThereIsNoPresetQuestion()
    {
        var prompt = Quote(1, 2).ToPrompt(null);

        Assert.StartsWith("Selected in the diff of", prompt, StringComparison.Ordinal);
        Assert.Contains("lines 40-41", prompt, StringComparison.Ordinal);
    }
}
