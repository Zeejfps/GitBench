using GitBench.Features.Markdown.Rendering;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;

namespace GitBench.Tests.Markdown;

// Pins RichTextLayout's contract: a greedy run-aware wrap whose break behavior is exactly
// TextWrapper's. All geometry uses the SyntheticTextMeasurer (8px per UTF-16 unit, 16px line
// height) unless a test opts into the style-aware StyledMeasurer, which varies advance by
// FontWeight and line height by FontSize — the ITextMeasurer seam receives the TextStyle per
// call, so per-run metrics are exactly what the real canvas provides.
//
// The reference for slicing is TextWrapper.WrapRanges, not Wrap: segments are slices over the
// run text and must tile it (trailing spaces at a soft break stay on the segment they follow;
// a '\n' sits in the one-character gap between lines). Wrap additionally collapses interior
// space sequences on wrapped lines, which a slice-based layout cannot do — so Wrap parity is
// asserted on single-space inputs, and WrapRanges parity (including a multi-space input) is
// asserted on exact slice boundaries.
public class RichTextLayoutTests
{
    private const float W = 8f;   // synthetic advance per UTF-16 unit
    private const float LineH = 16f;

    private static readonly TextStyle Plain = new();

    private static RecordingCanvas Canvas() => new(new SyntheticTextMeasurer());
    private static RecordingCanvas StyledCanvas() => new(new StyledMeasurer());

    private static RichTextRun Run(string text) => new(text, new TextStyle());
    private static RichTextRun Bold(string text) => new(text, new TextStyle { FontWeight = FontWeight.Bold });
    private static RichTextRun Sized(string text, float fontSize) => new(text, new TextStyle { FontSize = fontSize });

    private static RichTextLayoutResult Lay(float maxWidth, params RichTextRun[] runs) =>
        RichTextLayout.Layout(Canvas(), runs, maxWidth);

    private static string SliceOf(IReadOnlyList<RichTextRun> runs, RichTextSegment s) =>
        runs[s.RunIndex].Text.Substring(s.Start, s.Length);

    private static string LineText(IReadOnlyList<RichTextRun> runs, RichTextLine line) =>
        string.Concat(line.Segments.Select(s => SliceOf(runs, s)));

    private static List<string> LineTexts(IReadOnlyList<RichTextRun> runs, RichTextLayoutResult result) =>
        result.Lines.Select(l => LineText(runs, l)).ToList();

    // Style-aware synthetic metrics. Bold glyphs are wider (12px vs 8px) and line height follows
    // FontSize when set (else 16px), so tests can prove the layout measures every slice with its
    // own run's style rather than a single flattened one.
    private sealed class StyledMeasurer : ITextMeasurer
    {
        public float MeasureTextWidth(ReadOnlySpan<char> text, TextStyle style) => text.Length * AdvanceOf(style);

        public float MeasureTextPrefix(ReadOnlySpan<char> text, int prefixLength, TextStyle style) =>
            Math.Clamp(prefixLength, 0, text.Length) * AdvanceOf(style);

        public float MeasureTextLineHeight(TextStyle style) =>
            style.FontSize.IsSet ? style.FontSize.Value : 16f;

        private static float AdvanceOf(TextStyle style) =>
            style.FontWeight is { IsSet: true, Value: FontWeight.Bold } ? 12f : 8f;
    }

    // ---------- empty / trivial ----------

    [Fact]
    public void EmptyRunListProducesNoLinesAndNoHeight()
    {
        var result = Lay(400f);

        Assert.Empty(result.Lines);
        Assert.Equal(0f, result.Height);
        Assert.Equal(0f, result.MaxLineWidth);
    }

    [Fact]
    public void RunsWithNoTextAtAllProduceNoLines()
    {
        var result = Lay(400f, Run(""));

        Assert.Empty(result.Lines);
        Assert.Equal(0f, result.Height);
    }

    [Fact]
    public void SingleRunThatFitsIsOneLineWithOneSegment()
    {
        var runs = new[] { Run("hello") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        var line = Assert.Single(result.Lines);
        var seg = Assert.Single(line.Segments);
        Assert.Equal(0, seg.RunIndex);
        Assert.Equal(0, seg.Start);
        Assert.Equal(5, seg.Length);
        Assert.Equal(0f, seg.X);
        Assert.Equal(5 * W, seg.Width);
        Assert.Equal(5 * W, line.Width);
        Assert.Equal(LineH, line.Height);
        Assert.Equal(LineH, result.Height);
        Assert.Equal(5 * W, result.MaxLineWidth);
    }

    [Fact]
    public void NonPositiveMaxWidthLeavesTextUnwrapped()
    {
        var runs = new[] { Run("hello world") };
        var result = RichTextLayout.Layout(Canvas(), runs, 0f);

        Assert.Equal(new[] { "hello world" }, LineTexts(runs, result));
    }

    // ---------- space wrapping and slicing ----------

    [Fact]
    public void WrapsAtSpaces_TrailingSpaceStaysOnThePrecedingSegment()
    {
        // WrapRanges("aa bb cc", 40) → [0,6) "aa bb " and [6,8) "cc": the space at the soft
        // break belongs to the line it follows, so the slices tile the input.
        var runs = new[] { Run("aa bb cc") };
        var result = RichTextLayout.Layout(Canvas(), runs, 40f);

        Assert.Equal(new[] { "aa bb ", "cc" }, LineTexts(runs, result));
        var first = Assert.Single(result.Lines[0].Segments);
        Assert.Equal(6 * W, first.Width);
        var second = Assert.Single(result.Lines[1].Segments);
        Assert.Equal(6, second.Start);
        Assert.Equal(2 * W, second.Width);
        Assert.Equal(2 * LineH, result.Height);
    }

    [Fact]
    public void OverWideUnbreakableRunBreaksBetweenCodePoints()
    {
        var runs = new[] { Run("aaaaa") };
        var result = RichTextLayout.Layout(Canvas(), runs, 16f);

        Assert.Equal(new[] { "aa", "aa", "a" }, LineTexts(runs, result));
    }

    [Fact]
    public void OverWideWordStartsOnAFreshLine()
    {
        var runs = new[] { Run("aa bbbb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 16f);

        Assert.Equal(new[] { "aa ", "bb", "bb" }, LineTexts(runs, result));
    }

    // ---------- break opportunities: separators, CJK, kinsoku ----------

    [Fact]
    public void PathBreaksAfterSeparatorsNotMidSegment()
    {
        var runs = new[] { Run("usr/bin/sh") };
        var result = RichTextLayout.Layout(Canvas(), runs, 32f);

        Assert.Equal(new[] { "usr/", "bin/", "sh" }, LineTexts(runs, result));
    }

    [Fact]
    public void HyphenAndUnderscoreAreBreakOpportunities()
    {
        var runs = new[] { Run("a-b_c") };
        var result = RichTextLayout.Layout(Canvas(), runs, 16f);

        Assert.Equal(new[] { "a-", "b_", "c" }, LineTexts(runs, result));
    }

    [Fact]
    public void SeparatorDoesNotStartALine()
    {
        // '.' is kinsoku no-break-before: it stays glued to the segment it ends.
        var runs = new[] { Run("ab.cd") };
        var result = RichTextLayout.Layout(Canvas(), runs, 24f);

        Assert.Equal(new[] { "ab.", "cd" }, LineTexts(runs, result));
    }

    [Fact]
    public void CjkBreaksBetweenIdeographs()
    {
        var runs = new[] { Run("世界平和") };
        var result = RichTextLayout.Layout(Canvas(), runs, 16f);

        Assert.Equal(new[] { "世界", "平和" }, LineTexts(runs, result));
    }

    // ---------- multi-run lines ----------

    [Fact]
    public void MultiRunLineSegmentsSitAdjacentWithTheirOwnWidths()
    {
        var runs = new[] { Run("hello "), Run("world") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        var line = Assert.Single(result.Lines);
        Assert.Equal(2, line.Segments.Count);
        Assert.Equal((0, 0f, 6 * W), (line.Segments[0].RunIndex, line.Segments[0].X, line.Segments[0].Width));
        Assert.Equal((1, 6 * W, 5 * W), (line.Segments[1].RunIndex, line.Segments[1].X, line.Segments[1].Width));
        Assert.Equal(11 * W, line.Width);
    }

    [Fact]
    public void EachRunIsMeasuredWithItsOwnStyle()
    {
        // Bold advance is 12px under the StyledMeasurer, so the bold segment is wider and the
        // following x reflects it — a flattened single-style measure would put both at 8px.
        var runs = new[] { Run("ab"), Bold("cd") };
        var result = RichTextLayout.Layout(StyledCanvas(), runs, 800f);

        var line = Assert.Single(result.Lines);
        Assert.Equal(2 * 8f, line.Segments[0].Width);
        Assert.Equal(2 * 8f, line.Segments[1].X);
        Assert.Equal(2 * 12f, line.Segments[1].Width);
        Assert.Equal(2 * 8f + 2 * 12f, line.Width);
    }

    [Fact]
    public void BoldAdvanceMovesTheWrapPoint()
    {
        // Same text, same width: plain fits (5 × 8 = 40), bold does not (5 × 12 = 60) — the wrap
        // decision must be made in the run's own metrics.
        var plain = new[] { Run("aa bb") };
        Assert.Single(RichTextLayout.Layout(StyledCanvas(), plain, 40f).Lines);

        var bold = new[] { Bold("aa bb") };
        Assert.Equal(2, RichTextLayout.Layout(StyledCanvas(), bold, 40f).Lines.Count);
    }

    [Fact]
    public void RunBoundaryWithASpaceBreaksLikeTheSingleStyleText()
    {
        var runs = new[] { Run("hello "), Run("world") };
        var result = RichTextLayout.Layout(Canvas(), runs, 40f);

        Assert.Equal(new[] { "hello ", "world" }, LineTexts(runs, result));
        Assert.Equal(0, Assert.Single(result.Lines[0].Segments).RunIndex);
        Assert.Equal(1, Assert.Single(result.Lines[1].Segments).RunIndex);
    }

    [Fact]
    public void RunBoundaryAfterASeparatorIsABreakOpportunity()
    {
        var runs = new[] { Run("usr/"), Run("bin") };
        var result = RichTextLayout.Layout(Canvas(), runs, 32f);

        Assert.Equal(new[] { "usr/", "bin" }, LineTexts(runs, result));
    }

    [Fact]
    public void RunBoundaryAloneIsNotABreakOpportunity()
    {
        // "wo" + bold "rd" is the word "word": no break opportunity anywhere, wider than the
        // 24px line, so it splits between code points exactly where the single-style text would
        // ("wor" / "d") — never at the style seam just because a seam is there.
        var runs = new[] { Run("wo"), Bold("rd") };
        var result = RichTextLayout.Layout(Canvas(), runs, 24f);

        Assert.Equal(new[] { "wor", "d" }, LineTexts(runs, result));
        Assert.Equal(2, result.Lines[0].Segments.Count);
        Assert.Equal((0, 0, 2), (result.Lines[0].Segments[0].RunIndex, result.Lines[0].Segments[0].Start, result.Lines[0].Segments[0].Length));
        Assert.Equal((1, 0, 1), (result.Lines[0].Segments[1].RunIndex, result.Lines[0].Segments[1].Start, result.Lines[0].Segments[1].Length));
        var tail = Assert.Single(result.Lines[1].Segments);
        Assert.Equal((1, 1, 1), (tail.RunIndex, tail.Start, tail.Length));
    }

    [Fact]
    public void WordSplitAcrossRunsThatFitsStaysOnOneLine()
    {
        var runs = new[] { Run("wo"), Bold("rd") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        var line = Assert.Single(result.Lines);
        Assert.Equal("word", LineText(runs, line));
    }

    [Fact]
    public void EmptyRunDoesNotCreateABreakOpportunity()
    {
        var runs = new[] { Run("aa"), Run(""), Run("bb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 24f);

        Assert.Equal(new[] { "aab", "b" }, LineTexts(runs, result));
        Assert.DoesNotContain(result.Lines.SelectMany(l => l.Segments), s => s.RunIndex == 1);
    }

    // ---------- forced breaks: '\n' ----------

    [Fact]
    public void NewlineInsideARunForcesALineBreak()
    {
        var runs = new[] { Run("aa\nbb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        Assert.Equal(new[] { "aa", "bb" }, LineTexts(runs, result));
        // The '\n' itself (index 2) sits in the gap between the slices.
        Assert.Equal(3, Assert.Single(result.Lines[1].Segments).Start);
    }

    [Fact]
    public void HardBreakRunForcesALineBreakAndDrawsNothing()
    {
        // Markdown hard breaks arrive as dedicated "\n" runs (see MarkdownAst.InlineRun).
        var runs = new[] { Run("aa"), Run("\n"), Run("bb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        Assert.Equal(new[] { "aa", "bb" }, LineTexts(runs, result));
        Assert.DoesNotContain(result.Lines.SelectMany(l => l.Segments), s => s.RunIndex == 1);
    }

    [Fact]
    public void ConsecutiveNewlinesProduceAnEmptySegmentlessLine()
    {
        var runs = new[] { Run("a\n\nb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        Assert.Equal(3, result.Lines.Count);
        Assert.Empty(result.Lines[1].Segments);
        Assert.Equal(3 * LineH, result.Height);
    }

    [Fact]
    public void TrailingNewlineProducesATrailingEmptyLine()
    {
        // WrapRanges emits a zero-length trailing range for "a\n"; the layout mirrors it so a
        // paragraph ending in a hard break keeps its blank visual line.
        var runs = new[] { Run("a\n") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        Assert.Equal(2, result.Lines.Count);
        Assert.Empty(result.Lines[1].Segments);
        Assert.Equal(2 * LineH, result.Height);
    }

    // ---------- line height ----------

    [Fact]
    public void TallestRunOnALineGovernsItsHeight()
    {
        var runs = new[] { Run("small "), Sized("BIG", 24f) };
        var result = RichTextLayout.Layout(StyledCanvas(), runs, 800f);

        var line = Assert.Single(result.Lines);
        Assert.Equal(24f, line.Height);
        Assert.Equal(24f, result.Height);
    }

    [Fact]
    public void LineWithoutTheTallRunKeepsItsOwnHeight()
    {
        // "aaaa " (32px + 8px space) plus "BB" (16px) exceeds 48px, so the tall run wraps to its
        // own line: line heights are per line, not per paragraph.
        var runs = new[] { Run("aaaa "), Sized("BB", 24f) };
        var result = RichTextLayout.Layout(StyledCanvas(), runs, 48f);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(16f, result.Lines[0].Height);
        Assert.Equal(24f, result.Lines[1].Height);
        Assert.Equal(40f, result.Height);
    }

    [Fact]
    public void EmptyLineKeepsTheHeightOfTheRunWhoseNewlineProducedIt()
    {
        var runs = new[] { Sized("a\n\nb", 24f) };
        var result = RichTextLayout.Layout(StyledCanvas(), runs, 800f);

        Assert.Equal(3, result.Lines.Count);
        Assert.All(result.Lines, l => Assert.Equal(24f, l.Height));
        Assert.Equal(72f, result.Height);
    }

    [Fact]
    public void MaxLineWidthIsTheWidestLine()
    {
        var runs = new[] { Run("aa\nbbbb") };
        var result = RichTextLayout.Layout(Canvas(), runs, 800f);

        Assert.Equal(4 * W, result.MaxLineWidth);
    }

    // ---------- parity corpus: single-style runs against TextWrapper ----------

    // A representative slice of TextWrapperTests plus path/URL shapes. One single-style run
    // through RichTextLayout must split exactly where TextWrapper.Wrap splits the same text on
    // the same canvas. Wrap materializes lines without the soft-break trailing space that
    // WrapRanges (and the layout's slices) keep, so the comparison trims trailing spaces from
    // the reconstructed lines — the break positions are what must be byte-identical.
    [Theory]
    [InlineData("hello world", 800f)]
    [InlineData("hello world", 0f)]
    [InlineData("a\nb\nc", 80f)]
    [InlineData("a\n\nb", 80f)]
    [InlineData("aa bb cc", 40f)]
    [InlineData("aaaaa", 16f)]
    [InlineData("aa bbbb", 16f)]
    [InlineData("usr/bin/sh", 32f)]
    [InlineData("C:\\src\\a", 32f)]
    [InlineData("a-b_c", 16f)]
    [InlineData("x/aaaa", 16f)]
    [InlineData("ab.cd", 24f)]
    [InlineData("世界平和", 16f)]
    [InlineData("世界", 80f)]
    [InlineData("Hi世界", 16f)]
    [InlineData("あ。あ", 8f)]
    [InlineData("あ「い", 16f)]
    [InlineData("\U00020000\U00020000", 16f)]
    [InlineData("https://example.com/path/to/thing", 64f)]
    [InlineData("wrap at spaces and keep going until it wraps", 80f)]
    public void SingleStyleRunSplitsExactlyLikeTextWrapper(string text, float maxWidth)
    {
        var canvas = Canvas();
        var expected = new List<string>();
        TextWrapper.Wrap(canvas, text, Plain, maxWidth, expected);

        var runs = new[] { Run(text) };
        var actual = LineTexts(runs, RichTextLayout.Layout(canvas, runs, maxWidth))
            .Select(l => l.TrimEnd(' '))
            .ToList();

        Assert.Equal(expected, actual);
    }

    // Exact slice-boundary parity with TextWrapper.WrapRanges — the range-based variant is the
    // reference for slicing (trailing spaces, multi-space interiors, newline gaps), since a
    // caret-style consumer of the layout must find every index on exactly one line. Includes a
    // multi-space input where Wrap and WrapRanges genuinely diverge (Wrap collapses interior
    // runs of spaces on wrapped lines; slices cannot).
    [Theory]
    [InlineData("a  b c", 40f)]
    [InlineData("aa bb cc", 40f)]
    [InlineData("x/aaaa", 16f)]
    [InlineData("世界平和", 16f)]
    [InlineData("a\n\nb", 800f)]
    [InlineData("C:\\src\\a", 32f)]
    public void SingleStyleRunSlicesMatchWrapRanges(string text, float maxWidth)
    {
        var canvas = Canvas();
        var expected = new List<Range>();
        TextWrapper.WrapRanges(canvas, text, Plain, maxWidth, expected);

        var runs = new[] { Run(text) };
        var lines = RichTextLayout.Layout(canvas, runs, maxWidth).Lines;

        Assert.Equal(expected.Count, lines.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var (start, end) = (expected[i].Start.Value, expected[i].End.Value);
            if (start == end)
            {
                Assert.Empty(lines[i].Segments);
                continue;
            }

            Assert.NotEmpty(lines[i].Segments);
            Assert.Equal(start, lines[i].Segments[0].Start);
            var last = lines[i].Segments[^1];
            Assert.Equal(end, last.Start + last.Length);
        }
    }
}
