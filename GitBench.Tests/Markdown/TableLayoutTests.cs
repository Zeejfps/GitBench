using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;

namespace GitBench.Tests.Markdown;

// Pins TableLayout, the pure column-sizing engine, under the SyntheticTextMeasurer (8px per
// UTF-16 unit, 16px line height) — exact pixel numbers throughout.
//
// The pinned contract:
// - MinContentWidth = the widest UNBREAKABLE CHUNK under RichTextLayout's break rules: spaces
//   separate chunks and belong to none; a break is allowed after the separators / \ - _ . :
//   (the separator ends its chunk); wide (CJK) code points break between characters; kinsoku
//   glues closing punctuation to its neighbor; '\n' terminates a chunk; a run seam is NOT a
//   break by itself; every slice measures in its own run's style. This is deliberately NOT
//   "layout at a tiny width" — RichTextLayout force-splits over-wide chunks between code
//   points, which would yield single-glyph widths, not chunk widths.
// - MaxContentWidth = the unwrapped width (RichTextLayout at maxWidth <= 0; '\n' still breaks).
// - Measure: per column, min/max = the column-wise maxima over header + row cells;
//   contentAvail = availableWidth - 2*cellPaddingX*columns. Sigma-max <= contentAvail -> AllMax.
//   Else Sigma-min <= contentAvail -> Distributed: width_i = min_i + (contentAvail - Sigma-min)
//   * (max_i - min_i) / Sigma(max - min), EXACT float proportions with no pixel rounding, so
//   the widths sum to contentAvail exactly and a column at max (or empty) never grows. Else
//   OverflowAtMin: widths = min (the widget scrolls). Non-positive availableWidth means
//   unconstrained -> AllMax. TableWidth = Sigma-widths + padding (== availableWidth exactly in
//   Distributed mode). Alignments pass through untouched.
public class TableLayoutTests
{
    private const float W = 8f;

    private static readonly TextStyle Plain = new();

    private static RecordingCanvas Canvas() => new(new SyntheticTextMeasurer());

    private static RichTextRun Run(string text) => new(text, Plain);

    private static IReadOnlyList<RichTextRun> Cell(string text) => new[] { Run(text) };

    private static IReadOnlyList<IReadOnlyList<RichTextRun>> Cells(params string[] texts) =>
        texts.Select(Cell).ToArray();

    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> Rows(
        params string[][] rows) => rows.Select(r => Cells(r)).ToArray();

    private static float Min(params RichTextRun[] runs) =>
        TableLayout.MinContentWidth(Canvas(), runs);

    private static float Min(string text) => Min(Run(text));

    private static float Max(params RichTextRun[] runs) =>
        TableLayout.MaxContentWidth(Canvas(), runs);

    private static float Max(string text) => Max(Run(text));

    private static TableColumns Measure(
        float availableWidth,
        IReadOnlyList<IReadOnlyList<RichTextRun>> header,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> rows,
        IReadOnlyList<ColumnAlignment>? alignments = null,
        float cellPaddingX = 0f)
    {
        alignments ??= Enumerable.Repeat(ColumnAlignment.None, header.Count).ToArray();
        return TableLayout.Measure(Canvas(), header, rows, alignments, availableWidth, cellPaddingX);
    }

    private static void AssertWidths(TableColumns result, params float[] expected)
    {
        Assert.Equal(expected.Length, result.Widths.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], result.Widths[i], 3);
    }

    // Style-aware synthetic metrics, the RichTextLayoutTests house pattern: bold advances 12px
    // instead of 8px, so a measurement that flattens runs into one style gets a wrong total.
    private sealed class StyledMeasurer : ITextMeasurer
    {
        public float MeasureTextWidth(ReadOnlySpan<char> text, TextStyle style) =>
            text.Length * AdvanceOf(style);

        public float MeasureTextPrefix(ReadOnlySpan<char> text, int prefixLength, TextStyle style) =>
            Math.Clamp(prefixLength, 0, text.Length) * AdvanceOf(style);

        public float MeasureTextLineHeight(TextStyle style) => 16f;

        private static float AdvanceOf(TextStyle style) =>
            style.FontWeight is { IsSet: true, Value: FontWeight.Bold } ? 12f : 8f;
    }

    // ---------- min-content: the widest unbreakable chunk ----------

    [Fact]
    public void MinContentOfAnEmptyCellIsZero()
    {
        Assert.Equal(0f, Min(Array.Empty<RichTextRun>()));
        Assert.Equal(0f, Min(Run("")));
    }

    [Fact]
    public void MinContentOfASingleWordIsItsFullWidth()
    {
        Assert.Equal(5 * W, Min("hello"));
    }

    [Fact]
    public void SpacesSeparateChunksAndBelongToNone()
    {
        // "hello" and "world" are both 5 chars; the space is a break opportunity and is NOT part
        // of either chunk — min-content is 40, not 48 ("hello ") and not 88 (unwrapped).
        Assert.Equal(5 * W, Min("hello world"));
    }

    [Fact]
    public void TheWidestChunkWins()
    {
        Assert.Equal(6 * W, Min("a bbbbbb c"));
    }

    [Fact]
    public void SeparatorPunctuationEndsAChunkOnItsTrailingSide()
    {
        // "path/to/name" chunks as "path/", "to/", "name" (break allowed AFTER '/'): widest is
        // "path/" = 5 chars. Long tokens with separators wrap at their natural boundaries.
        Assert.Equal(5 * W, Min("path/to/name"));
    }

    [Fact]
    public void AnUnbreakableTokenIsItsOwnMinContent()
    {
        // The table-sizing pathology from the plan: a hash-like token has no break opportunity,
        // so its full width is the floor the column can never shrink below.
        Assert.Equal(12 * W, Min("deadbeefcafe"));
    }

    [Fact]
    public void HardLineBreaksTerminateChunks()
    {
        Assert.Equal(4 * W, Min("aaaa\nbb"));
    }

    [Fact]
    public void CjkBreaksBetweenCodePointsSoEachCharacterIsAChunk()
    {
        Assert.Equal(W, Min("日本語"));
    }

    [Fact]
    public void KinsokuGluesClosingPunctuationToTheChunkBeforeIt()
    {
        // CJK allows a break between 日 and 本, but ')' must not start a line, so the widest
        // chunk is "本)" — 2 units, not 1.
        Assert.Equal(2 * W, Min("日本)"));
    }

    [Fact]
    public void ARunSeamIsNotABreakOpportunity()
    {
        // "ab"+"cd" reads as the single chunk "abcd"; with a space at the seam the chunks are
        // "ab" and "cd".
        Assert.Equal(4 * W, Min(Run("ab"), Run("cd")));
        Assert.Equal(2 * W, Min(Run("ab "), Run("cd")));
    }

    [Fact]
    public void ATrailingSpaceIsExcludedFromMinContent()
    {
        Assert.Equal(2 * W, Min("ab "));
    }

    [Fact]
    public void MinContentMeasuresEachSliceInItsOwnRunStyle()
    {
        // One chunk across a plain->bold seam: 2*8 + 2*12 = 40 under the StyledMeasurer. A
        // flattened single-style measurement would report 32 or 48.
        var canvas = new RecordingCanvas(new StyledMeasurer());
        var runs = new[]
        {
            Run("ab"),
            new RichTextRun("cd", new TextStyle { FontWeight = FontWeight.Bold }),
        };

        Assert.Equal(40f, TableLayout.MinContentWidth(canvas, runs));
    }

    // ---------- max-content: the unwrapped width ----------

    [Fact]
    public void MaxContentOfAnEmptyCellIsZero()
    {
        Assert.Equal(0f, Max(Array.Empty<RichTextRun>()));
    }

    [Fact]
    public void MaxContentIsTheUnwrappedWidth()
    {
        Assert.Equal(11 * W, Max("hello world"));
    }

    [Fact]
    public void HardLineBreaksBoundMaxContent()
    {
        Assert.Equal(4 * W, Max("aa\nbbbb"));
    }

    [Fact]
    public void MaxContentMeasuresEachRunInItsOwnStyle()
    {
        var canvas = new RecordingCanvas(new StyledMeasurer());
        var runs = new[]
        {
            Run("ab"),
            new RichTextRun("cd", new TextStyle { FontWeight = FontWeight.Bold }),
        };

        Assert.Equal(40f, TableLayout.MaxContentWidth(canvas, runs));
    }

    [Fact]
    public void MinContentNeverExceedsMaxContent()
    {
        foreach (var text in new[] { "hello world", "path/to/name", "deadbeefcafe", "日本語", "a\nbb" })
        {
            Assert.True(Min(text) <= Max(text), $"min > max for \"{text}\"");
        }
    }

    // ---------- Measure: all-max mode ----------

    [Fact]
    public void AllColumnsSitAtMaxContentWhenTheyFit()
    {
        var result = Measure(400f, Cells("ab", "cd"), Rows(new[] { "e", "fgh" }));

        AssertWidths(result, 2 * W, 3 * W);
        Assert.Equal(TableFitMode.AllMax, result.Mode);
        Assert.Equal(5 * W, result.TableWidth, 3);
    }

    [Fact]
    public void HeaderCellsParticipateInColumnWidths()
    {
        var result = Measure(400f, Cells("abcdef", "x"), Rows(new[] { "a", "yy" }));

        AssertWidths(result, 6 * W, 2 * W);
        Assert.Equal(TableFitMode.AllMax, result.Mode);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void NonPositiveAvailableWidthMeansUnconstrainedAllMax(float availableWidth)
    {
        var result = Measure(availableWidth, Cells("hello world"), Rows());

        AssertWidths(result, 11 * W);
        Assert.Equal(TableFitMode.AllMax, result.Mode);
        Assert.Equal(11 * W, result.TableWidth, 3);
    }

    [Fact]
    public void PaddingCountsAgainstTheAvailableWidth()
    {
        // Column maxes 16 + 24 plus 8px padding on each side of each column = 72 total. At
        // exactly 72 the table fits at max; one pixel less and (min == max here) even the min
        // widths overflow.
        var header = Cells("ab", "fgh");
        var rows = Rows(new[] { "e", "id" });

        var fits = Measure(72f, header, rows, cellPaddingX: 8f);
        AssertWidths(fits, 2 * W, 3 * W);
        Assert.Equal(TableFitMode.AllMax, fits.Mode);
        Assert.Equal(72f, fits.TableWidth, 3);

        var overflows = Measure(71f, header, rows, cellPaddingX: 8f);
        AssertWidths(overflows, 2 * W, 3 * W);
        Assert.Equal(TableFitMode.OverflowAtMin, overflows.Mode);
        Assert.Equal(72f, overflows.TableWidth, 3);
    }

    // ---------- Measure: proportional distribution ----------

    // Column mins/maxes for the distribution cases:
    //   col 0: "aaaa aaaa" -> min 32, max 72 (growth 40)
    //   col 1: "bb bb"     -> min 16, max 40 (growth 24)
    // Sigma-min = 48, Sigma-max = 112, Sigma-growth = 64.
    private static IReadOnlyList<IReadOnlyList<RichTextRun>> GrowHeader => Cells("h1", "h2");
    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> GrowRows =>
        Rows(new[] { "aaaa aaaa", "bb bb" });

    [Fact]
    public void RemainderIsDistributedProportionallyToMaxMinusMin()
    {
        // contentAvail 80: remainder 32 over growths (40, 24) -> +20 and +12.
        var result = Measure(80f, GrowHeader, GrowRows);

        AssertWidths(result, 52f, 28f);
        Assert.Equal(TableFitMode.Distributed, result.Mode);
        Assert.Equal(80f, result.TableWidth, 3);
    }

    [Fact]
    public void DistributionUsesExactFractionalPixelsWithNoRounding()
    {
        // The pinned rounding policy: none. contentAvail 100 -> remainder 52 -> shares 32.5 and
        // 19.5; widths keep the fraction and sum to the available width exactly.
        var result = Measure(100f, GrowHeader, GrowRows);

        AssertWidths(result, 64.5f, 35.5f);
        Assert.Equal(100f, result.Widths[0] + result.Widths[1], 3);
        Assert.Equal(TableFitMode.Distributed, result.Mode);
        Assert.Equal(100f, result.TableWidth, 3);
    }

    [Fact]
    public void DistributedWidthsSumToTheAvailableContentWidthWithPadding()
    {
        // Padding is carved out first: avail 112 with 8px per-side padding on 2 columns leaves
        // contentAvail 80 -> the same (52, 28) split; TableWidth is the full available width.
        var result = Measure(112f, GrowHeader, GrowRows, cellPaddingX: 8f);

        AssertWidths(result, 52f, 28f);
        Assert.Equal(TableFitMode.Distributed, result.Mode);
        Assert.Equal(112f, result.TableWidth, 3);
    }

    [Fact]
    public void AColumnAlreadyAtItsMaxGetsNoExtraWidth()
    {
        // col 1 ("cc") has min == max == 16: all 12 remainder pixels go to col 0.
        var result = Measure(
            60f, Cells("a", "b"), Rows(new[] { "aaaa bbbb", "cc" }));

        AssertWidths(result, 44f, 16f);
        Assert.Equal(TableFitMode.Distributed, result.Mode);
    }

    [Fact]
    public void AtExactlyTheMinWidthSumColumnsSitAtMinContent()
    {
        // Sigma-min boundary is Distributed (remainder 0), not overflow.
        var result = Measure(48f, GrowHeader, GrowRows);

        AssertWidths(result, 32f, 16f);
        Assert.Equal(TableFitMode.Distributed, result.Mode);
        Assert.Equal(48f, result.TableWidth, 3);
    }

    // ---------- Measure: overflow mode ----------

    [Fact]
    public void BelowTheMinWidthSumTheTableOverflowsAtMinWidths()
    {
        var result = Measure(47.5f, GrowHeader, GrowRows);

        AssertWidths(result, 32f, 16f);
        Assert.Equal(TableFitMode.OverflowAtMin, result.Mode);
        Assert.Equal(48f, result.TableWidth, 3); // wider than available: the widget scrolls
    }

    [Fact]
    public void OverflowTableWidthIncludesThePadding()
    {
        // contentAvail = 60 - 32 = 28 < Sigma-min 48 -> overflow; the drawn width keeps padding.
        var result = Measure(60f, GrowHeader, GrowRows, cellPaddingX: 8f);

        AssertWidths(result, 32f, 16f);
        Assert.Equal(TableFitMode.OverflowAtMin, result.Mode);
        Assert.Equal(80f, result.TableWidth, 3);
    }

    // ---------- Measure: degenerate shapes ----------

    [Fact]
    public void AnEmptyColumnGetsZeroWidth()
    {
        var result = Measure(
            400f, Cells("ab", ""), Rows(new[] { "cd", "" }, new[] { "e", "" }));

        AssertWidths(result, 2 * W, 0f);
        Assert.Equal(TableFitMode.AllMax, result.Mode);
    }

    [Fact]
    public void ASingleColumnTakesAllTheRemainder()
    {
        var distributed = Measure(60f, Cells("hello world"), Rows());
        AssertWidths(distributed, 60f);
        Assert.Equal(TableFitMode.Distributed, distributed.Mode);

        var atMax = Measure(200f, Cells("hello world"), Rows());
        AssertWidths(atMax, 11 * W);
        Assert.Equal(TableFitMode.AllMax, atMax.Mode);
    }

    [Fact]
    public void FifteenColumnsAllFitAtMax()
    {
        var cells = Enumerable.Range(0, 15).Select(i => ((char)('a' + i)).ToString()).ToArray();
        var result = Measure(400f, Cells(cells), Rows(cells));

        Assert.Equal(15, result.Widths.Count);
        Assert.All(result.Widths, w => Assert.Equal(W, w, 3));
        Assert.Equal(TableFitMode.AllMax, result.Mode);
        Assert.Equal(15 * W, result.TableWidth, 3);
    }

    [Fact]
    public void AHeaderOnlyTableSizesFromTheHeaderAlone()
    {
        // The streaming shape: header + delimiter parsed, no data rows yet.
        var result = Measure(400f, Cells("abc", "d"), Rows());

        AssertWidths(result, 3 * W, W);
        Assert.Equal(TableFitMode.AllMax, result.Mode);
    }

    // ---------- upstream assumption: cells are rectangular ----------

    [Fact]
    public void ParserPadsAndTruncatesRaggedRowsToTheHeaderColumnCount()
    {
        // TableLayout assumes rectangular cells; BasicMarkdownParser guarantees it (short rows
        // pad with empty cells, long rows truncate). Pinned here because the sizing engine's
        // contract depends on it.
        var doc = new BasicMarkdownParser().Parse("| a | b |\n|---|---|\n| 1 |\n| x | y | z |");

        var table = Assert.IsType<TableBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(2, table.Header.Count);
        Assert.All(table.Rows, row => Assert.Equal(2, row.Count));
        Assert.Empty(table.Rows[0][1]);                        // short row padded with an empty cell
        Assert.Equal("y", Assert.Single(table.Rows[1][1]).Text); // long row truncated
    }
}
