using System;
using System.Collections.Generic;
using System.Linq;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using Xunit;

namespace GitBench.Tests;

/// <summary>The buffer every other part of the editor is built on: the text and the line index still agreeing after an edit.</summary>
public sealed class EditorTextDocumentTests
{
    private static TextEdit Edit(int startLine, int startColumn, int endLine, int endColumn, string replacement) =>
        new(new TextRange(TextPosition.At(startLine, startColumn), TextPosition.At(endLine, endColumn)), replacement);

    private static string Line(TextDocument document, int line) => document.Line(new FileLine(line));

    [Fact]
    public void AnEmptyDocumentIsOneEmptyLine()
    {
        var document = TextDocument.FromText(string.Empty);

        Assert.Equal(1, document.LineCount);
        Assert.Equal(string.Empty, Line(document, 1));
        Assert.Equal(TextPosition.At(1, 0), document.End);
        Assert.False(document.EndsWithNewline);
    }

    [Fact]
    public void InsertingWithinALineLeavesEveryOtherLineWhereItWas()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");

        var inverse = document.Apply(Edit(2, 1, 2, 1, "XY"));

        Assert.Equal("one\ntXYwo\nthree", document.Text);
        Assert.Equal(3, document.LineCount);
        Assert.Equal("one", Line(document, 1));
        Assert.Equal("three", Line(document, 3));

        document.Apply(inverse);
        Assert.Equal("one\ntwo\nthree", document.Text);
    }

    [Fact]
    public void DeletingWithinALine()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");

        var inverse = document.Apply(Edit(3, 1, 3, 4, string.Empty));

        Assert.Equal("one\ntwo\nte", document.Text);
        Assert.Equal("hre", inverse.Replacement);

        document.Apply(inverse);
        Assert.Equal("one\ntwo\nthree", document.Text);
    }

    [Fact]
    public void ReplacingAcrossALineBoundaryJoinsTheLines()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");

        var inverse = document.Apply(Edit(1, 2, 3, 2, "-"));

        Assert.Equal("on-ree", document.Text);
        Assert.Equal(1, document.LineCount);

        document.Apply(inverse);
        Assert.Equal("one\ntwo\nthree", document.Text);
        Assert.Equal(3, document.LineCount);
    }

    [Fact]
    public void InsertingALineBreakSplitsALineInTwo()
    {
        var document = TextDocument.FromText("onetwo");

        document.Apply(Edit(1, 3, 1, 3, "\n"));

        Assert.Equal(2, document.LineCount);
        Assert.Equal("one", Line(document, 1));
        Assert.Equal("two", Line(document, 2));
    }

    [Fact]
    public void AnEditSpanningTheWholeDocumentReplacesIt()
    {
        var document = TextDocument.FromText("one\ntwo\nthree");
        var whole = new TextRange(TextPosition.At(1, 0), document.End);

        var inverse = document.Apply(new TextEdit(whole, "alpha\nbeta"));

        Assert.Equal("alpha\nbeta", document.Text);
        Assert.Equal(2, document.LineCount);

        document.Apply(inverse);
        Assert.Equal("one\ntwo\nthree", document.Text);
        Assert.Equal(3, document.LineCount);
    }

    [Theory]
    [InlineData("a\nb\nc")]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\rb\rc")]
    public void EveryLineBreakStyleCountsAsOneBreakAndSurvivesUntouched(string text)
    {
        var document = TextDocument.FromText(text);

        Assert.Equal(3, document.LineCount);
        Assert.Equal("a", Line(document, 1));
        Assert.Equal("b", Line(document, 2));
        Assert.Equal("c", Line(document, 3));
        Assert.Equal(text, document.Text);
        Assert.False(document.EndsWithNewline);
    }

    [Theory]
    [InlineData("a\n")]
    [InlineData("a\r\n")]
    [InlineData("a\r")]
    public void ATrailingTerminatorOpensAnEmptyLastLine(string text)
    {
        var document = TextDocument.FromText(text);

        Assert.Equal(2, document.LineCount);
        Assert.Equal("a", Line(document, 1));
        Assert.Equal(string.Empty, Line(document, 2));
        Assert.True(document.EndsWithNewline);
        Assert.Equal(text, document.Text);
    }

    [Fact]
    public void AFileWithNoTrailingNewlineIsNotTheSameDocumentAsOneWithIt()
    {
        var withNewline = TextDocument.FromText("a\n");
        var without = TextDocument.FromText("a");

        Assert.NotEqual(withNewline.LineCount, without.LineCount);
        Assert.True(withNewline.EndsWithNewline);
        Assert.False(without.EndsWithNewline);
        Assert.Equal("a\n", withNewline.Text);
        Assert.Equal("a", without.Text);
    }

    [Fact]
    public void MixedTerminatorsInOneFileAreEachOneBreak()
    {
        var document = TextDocument.FromText("a\r\nb\nc\rd");

        Assert.Equal(4, document.LineCount);
        Assert.Equal("b", Line(document, 2));
        Assert.Equal("c", Line(document, 3));
        Assert.Equal("d", Line(document, 4));
    }

    [Fact]
    public void ALineFeedTypedAfterALoneCarriageReturnBecomesOneBreak()
    {
        var document = TextDocument.FromText("a\rb");

        document.Apply(Edit(2, 0, 2, 0, "\n"));

        Assert.Equal("a\r\nb", document.Text);
        Assert.Equal(2, document.LineCount);
        Assert.Equal("a", Line(document, 1));
        Assert.Equal("b", Line(document, 2));
    }

    [Fact]
    public void ACarriageReturnTypedBeforeALineFeedBecomesOneBreak()
    {
        var document = TextDocument.FromText("a\nb");

        var inverse = document.Apply(Edit(1, 1, 1, 1, "x\r"));

        Assert.Equal("ax\r\nb", document.Text);
        Assert.Equal(2, document.LineCount);
        Assert.Equal("ax", Line(document, 1));
        Assert.Equal("b", Line(document, 2));

        document.Apply(inverse);
        Assert.Equal("a\nb", document.Text);
        Assert.Equal(2, document.LineCount);
    }

    [Fact]
    public void DeletingBetweenACarriageReturnAndALineFeedLeavesOneBreak()
    {
        var document = TextDocument.FromText("a\rX\nb");
        Assert.Equal(3, document.LineCount);

        var inverse = document.Apply(Edit(2, 0, 2, 1, string.Empty));

        Assert.Equal("a\r\nb", document.Text);
        Assert.Equal(2, document.LineCount);
        Assert.Equal("b", Line(document, 2));

        document.Apply(inverse);
        Assert.Equal("a\rX\nb", document.Text);
        Assert.Equal(3, document.LineCount);
    }

    [Fact]
    public void SliceReturnsTheCharactersBetweenTwoPositionsTerminatorsIncluded()
    {
        var document = TextDocument.FromText("one\r\ntwo\nthree");

        Assert.Equal("ne\r\ntw", document.Slice(new TextRange(TextPosition.At(1, 1), TextPosition.At(2, 2))));
        Assert.Equal(string.Empty, document.Slice(TextRange.Caret(TextPosition.At(2, 1))));
        Assert.Equal(document.Text, document.Slice(new TextRange(TextPosition.At(1, 0), document.End)));
    }

    [Fact]
    public void PositionsOutsideTheDocumentAreClampedIntoIt()
    {
        var document = TextDocument.FromText("ab\ncd");

        Assert.Equal(TextPosition.At(2, 2), document.Clamp(TextPosition.At(99, 99)));
        Assert.Equal(TextPosition.At(1, 0), document.Clamp(TextPosition.At(0, -5)));
        Assert.Equal(TextPosition.At(1, 2), document.Clamp(TextPosition.At(1, 40)));

        var pastTheEnd = new TextRange(TextPosition.At(9, 3), TextPosition.At(12, 1));
        Assert.True(document.Clamp(pastTheEnd).IsEmpty);
        Assert.Equal("d", document.Slice(new TextRange(TextPosition.At(2, 1), TextPosition.At(40, 40))));
    }

    [Fact]
    public void AnEditWithAnOutOfRangeRangeIsAppliedToTheClampedRange()
    {
        var document = TextDocument.FromText("ab\ncd");

        document.Apply(Edit(2, 40, 90, 90, "!"));

        Assert.Equal("ab\ncd!", document.Text);
    }

    [Fact]
    public void TheRevisionMovesOncePerEditAndNotAtAllForAnEditThatChangesNothing()
    {
        var document = TextDocument.FromText("ab");
        Assert.Equal(0, document.Revision);

        document.Apply(Edit(1, 1, 1, 1, "-"));
        Assert.Equal(1, document.Revision);

        var inverse = document.Apply(Edit(1, 2, 1, 2, string.Empty));
        Assert.Equal(1, document.Revision);
        Assert.Equal(string.Empty, inverse.Replacement);
        Assert.True(inverse.Range.IsEmpty);
    }

    [Fact]
    public void TheInverseOfAnEditRestoresTheTextAndTheLineCount()
    {
        var document = TextDocument.FromText("alpha\r\nbeta\ngamma\rdelta");
        var original = document.Text;
        var lines = document.LineCount;

        var inverses = new[]
        {
            document.Apply(Edit(2, 2, 3, 3, "\n\n")),
            document.Apply(Edit(1, 0, 1, 0, "prefix\r\n")),
            document.Apply(Edit(1, 3, 2, 1, "-")),
        };

        foreach (var inverse in inverses.Reverse())
            document.Apply(inverse);

        Assert.Equal(original, document.Text);
        Assert.Equal(lines, document.LineCount);
    }

    [Fact]
    public void AnEditInTheMiddleOfALargeDocumentRewritesAHandfulOfPiecesAndNoText()
    {
        var document = LargeDocument(20_000);
        Assert.Equal(1, document.PieceCount);

        document.Apply(Edit(10_000, 0, 10_000, 0, "X"));

        Assert.True(document.PieceCount <= 4, $"an insert grew the piece table to {document.PieceCount}");
        Assert.Equal(1, document.AppendedCharCount);
        Assert.Equal(20_000, document.LineCount);
        Assert.Equal("line 1", Line(document, 1));
        Assert.Equal("Xline 10000", Line(document, 10_000));
        Assert.Equal("line 20000", Line(document, 20_000));
    }

    [Fact]
    public void ARunOfKeystrokesAppendsTheKeystrokesAndNothingElse()
    {
        var document = LargeDocument(5_000);

        for (var i = 0; i < 200; i++)
            document.Apply(Edit(2_500, i, 2_500, i, "x"));

        Assert.Equal(200, document.AppendedCharCount);
        Assert.True(document.PieceCount <= 3 * 200, $"200 keystrokes left {document.PieceCount} pieces");
        Assert.Equal(new string('x', 200) + "line 2500", Line(document, 2_500));
        Assert.Equal(5_000, document.LineCount);
    }

    [Fact]
    public void DeletingALineRangeLeavesTheLinesAroundItIntact()
    {
        var document = LargeDocument(1_000);

        document.Apply(Edit(400, 0, 500, 0, string.Empty));

        Assert.Equal(900, document.LineCount);
        Assert.Equal("line 399", Line(document, 399));
        Assert.Equal("line 500", Line(document, 400));
    }

    [Fact]
    public void RandomEditsAgreeWithSplicingTheStringAndSplittingItAgain()
    {
        var random = new Random(20260903);
        var replacements = new[] { "", "a", "xy", "\n", "\r\n", "\r", "ab\ncd", "\nx", "y\r", "p\r\nq", "  " };
        var expected = "one\r\ntwo\nthree\rfour\n";
        var document = TextDocument.FromText(expected);

        for (var step = 0; step < 400; step++)
        {
            var lines = SplitLines(expected);
            var startLine = random.Next(lines.Count);
            var startColumn = random.Next(lines[startLine].Text.Length + 1);
            var endLine = random.Next(startLine, lines.Count);
            var endColumn = endLine == startLine
                ? random.Next(startColumn, lines[endLine].Text.Length + 1)
                : random.Next(lines[endLine].Text.Length + 1);
            var replacement = replacements[random.Next(replacements.Length)];

            var start = lines[startLine].Start + startColumn;
            var end = lines[endLine].Start + endColumn;
            expected = expected[..start] + replacement + expected[end..];

            document.Apply(new TextEdit(
                new TextRange(TextPosition.At(startLine + 1, startColumn), TextPosition.At(endLine + 1, endColumn)),
                replacement));

            Assert.Equal(expected, document.Text);

            var expectedLines = SplitLines(expected);
            Assert.Equal(expectedLines.Count, document.LineCount);
            for (var i = 0; i < expectedLines.Count; i++)
                Assert.Equal(expectedLines[i].Text, Line(document, i + 1));
        }
    }

    private static List<(int Start, string Text)> SplitLines(string text)
    {
        var lines = new List<(int, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            lines.Add((start, text[start..i]));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add((start, text[start..]));
        return lines;
    }

    private static TextDocument LargeDocument(int lines) =>
        TextDocument.FromText(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line {i}")));
}
