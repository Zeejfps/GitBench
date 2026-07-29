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
        new DiffRow.Line(DiffLineKind.Context, "40", "40", "public void Run()", 17),
        new DiffRow.Line(DiffLineKind.Context, "41", "41", "{", 1),
        new DiffRow.Line(DiffLineKind.Removed, "42", "", "    Legacy();", 13),
        new DiffRow.Line(DiffLineKind.Added, "", "42", "    Modern();", 13),
        new DiffRow.Line(DiffLineKind.Context, "43", "43", "}", 1),
    ];

    private static DiffSelectionQuote Quote(int fromRow, int toRow, string path = "src/Runner.cs") =>
        DiffSelectionQuote.Build(
            Rows,
            new DiffTextPos(fromRow, 0),
            new DiffTextPos(toRow, Rows[toRow] is DiffRow.Line line ? line.Text.Length : 0),
            path)!;

    [Fact]
    public void AnAddedSelection_CarriesThePathTheLineAndTheSide()
    {
        var quote = Quote(4, 4);

        Assert.Equal("src/Runner.cs", quote.Path);
        Assert.Equal(42, quote.StartLine);
        Assert.Equal(42, quote.EndLine);
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
        Assert.Equal(42, quote.StartLine);
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
        Assert.Equal(40, quote.StartLine);
        Assert.Equal(43, quote.EndLine);
    }

    // The clipboard's own extractor, not a second one: the text handed to the model is exactly what
    // Ctrl+C would have produced — no gutters, no +/- markers, and the "@@" bar dropped.
    [Fact]
    public void TheText_IsWhatTheCopyPipelineProduces()
    {
        var start = new DiffTextPos(0, 0);
        var end = new DiffTextPos(5, 1);

        var quote = DiffSelectionQuote.Build(Rows, start, end, "src/Runner.cs")!;

        Assert.Equal(DiffSelectionModel.BuildCopyText(Rows, start, end), quote.Text);
        Assert.DoesNotContain("@@", quote.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("+", quote.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectionOverNoCodeLines_IsNoQuestion()
    {
        Assert.Null(DiffSelectionQuote.Build(Rows, new DiffTextPos(0, 0), new DiffTextPos(0, 0), "x.cs"));
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
