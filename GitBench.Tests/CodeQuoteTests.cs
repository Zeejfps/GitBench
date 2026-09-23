using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>What a selection in the editor sends to the agent: the lines it really covers, and the
/// code as the file has it.</summary>
public sealed class CodeQuoteTests
{
    private static readonly string[] Lines = Enumerable.Range(1, 20).Select(n => $"line {n}").ToArray();

    // Joins the document's lines the way the editor's slice does, for a range over them.
    private static string Slice(TextRange range)
    {
        var from = range.Start.Line.Value - 1;
        var to = Math.Min(range.End.Line.Value - 1, Lines.Length - 1);
        var parts = new List<string>();
        for (var i = from; i <= to; i++)
        {
            var line = Lines[i];
            var start = i == from ? range.Start.Column.Value : 0;
            var end = i == range.End.Line.Value - 1 ? Math.Min(range.End.Column.Value, line.Length) : line.Length;
            parts.Add(line[start..end]);
        }

        return string.Join('\n', parts);
    }

    private static TextRange Range(int fromLine, int fromColumn, int toLine, int toColumn) =>
        new(TextPosition.At(fromLine, fromColumn), TextPosition.At(toLine, toColumn));

    [Fact]
    public void ASelectionEndingAtTheStartOfALine_DoesNotClaimThatLine()
    {
        var quote = CodeQuote.Of("C:/repo/a.cs", Range(3, 0, 6, 0), Slice)!;

        Assert.Equal(new FileLine(3), quote.Start);
        Assert.Equal(new FileLine(5), quote.End);
        Assert.Equal("line 3\nline 4\nline 5", quote.Text);
    }

    [Fact]
    public void ABareCaret_QuotesNothing() =>
        Assert.Null(CodeQuote.Of("C:/repo/a.cs", Range(4, 2, 4, 2), Slice));

    [Fact]
    public void Markdown_SaysWhereTheCodeIs_ThenFencesIt()
    {
        var quote = new CodeQuote.InFile("C:/repo/a.cs", new FileLine(7), new FileLine(7), "var x = 1;");

        Assert.Equal("Selected in `a.cs`, line 7:\n\n```\nvar x = 1;\n```", quote.ToMarkdown(_ => "a.cs"));
        Assert.Equal("a.cs:7", quote.Location(_ => "a.cs"));
    }

    [Fact]
    public void CodeThatHasAFenceInIt_GetsALongerOne()
    {
        var quote = new CodeQuote.InFile("C:/repo/README.md", new FileLine(1), new FileLine(3), "```\ncode\n```");

        Assert.Contains("````\n```\ncode\n```\n````", quote.ToMarkdown(_ => "README.md"));
    }

    [Fact]
    public void AQuoteFromADiff_SaysWhichSideOfTheChangeItIs()
    {
        CodeQuote quote = new CodeQuote.InDiff(
            new DiffSelectionQuote("src/a.cs", new FileLine(4), new FileLine(5), DiffQuoteSide.Removed, "old();\nolder();"));

        var markdown = quote.ToMarkdown(_ => throw new InvalidOperationException("A diff quote names its own path."));
        Assert.Contains("`src/a.cs`", markdown);
        Assert.Contains("lines 4-5", markdown);
        Assert.Contains("old();\nolder();", markdown);
        Assert.Equal("src/a.cs:4-5", quote.Location(_ => "unused"));
    }

    [Fact]
    public void TheUri_NamesTheLinesInItsFragment()
    {
        var quote = new CodeQuote.InFile(Path.Combine(Path.GetTempPath(), "a.cs"), new FileLine(10), new FileLine(14), "x");

        Assert.StartsWith("file:///", quote.Uri.ToString());
        Assert.EndsWith("a.cs#L10:14", quote.Uri.ToString());
    }
}
