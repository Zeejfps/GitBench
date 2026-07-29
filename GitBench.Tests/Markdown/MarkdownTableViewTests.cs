using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;

namespace GitBench.Tests.Markdown;

// Harness-driven geometry tests for MarkdownTableView. Synthetic metrics pin exact pixels:
// 8px per UTF-16 unit, 16px line height; the harness root is the view itself, so the table's
// top-left is (0, 600) and rows stack downward (y points up: lower on screen = smaller Bottom).
//
// The pinned vertical rhythm, from the view's design constants (CellPaddingX 8, CellPaddingY 4,
// HeaderRuleThickness 2, RowSeparatorThickness 1), for single-line rows:
//   header text bottom = 600 - 4 - 16          = 580
//   header row bottom  = 600 - 24              = 576, header rule occupies [574, 576]
//   row 1 text bottom  = 574 - 4 - 16          = 554
//   row 1 bottom       = 574 - 24              = 550, separator occupies [549, 550]
//   row 2 text bottom  = 549 - 4 - 16          = 529
// Horizontally, column i's content starts at Sigma_{j<i}(width_j + 16) + 8.
//
// MeasureWidth() is pinned to the MIN-content table width — that is the contract that makes the
// widget's HorizontalScrollArea nesting behave (fill-and-wrap when min fits the viewport,
// scroll at min widths when it doesn't; HorizontalScrollView sizes content to
// max(viewport, MeasureWidth())).
public class MarkdownTableViewTests
{
    private const float W = 8f;
    private const float LineH = 16f;
    private const float Top = 600f;
    private const float PadX = 8f;
    private const float PadY = 4f;

    private const uint HeaderColor = 0xFF111111;
    private const uint BodyColor = 0xFF222222;
    private const uint RuleColor = 0xFFAA0000;
    private const uint SeparatorColor = 0xFF00BB00;
    private const uint ChipBg = 0xFF2A2A3A;

    private static readonly TextStyle HeaderStyle = new()
    {
        TextColor = HeaderColor,
        FontWeight = FontWeight.Bold,
    };
    private static readonly TextStyle BodyStyle = new() { TextColor = BodyColor };

    private static IReadOnlyList<RichTextRun> HeaderCell(string text) =>
        new[] { new RichTextRun(text, HeaderStyle) };

    private static IReadOnlyList<RichTextRun> BodyCell(string text) =>
        new[] { new RichTextRun(text, BodyStyle) };

    private static IReadOnlyList<IReadOnlyList<RichTextRun>> Header(params string[] cells) =>
        cells.Select(HeaderCell).ToArray();

    private static IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> Rows(
        params string[][] rows) =>
        rows.Select(r => (IReadOnlyList<IReadOnlyList<RichTextRun>>)r.Select(BodyCell).ToArray())
            .ToArray();

    private static MarkdownTableView StandaloneView(
        IReadOnlyList<IReadOnlyList<RichTextRun>> header,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> rows,
        IReadOnlyList<ColumnAlignment>? alignments = null) =>
        new(new RecordingCanvas())
        {
            Header = header,
            Rows = rows,
            Alignments = alignments ?? Enumerable.Repeat(ColumnAlignment.None, header.Count).ToArray(),
            HeaderRuleColor = RuleColor,
            RowSeparatorColor = SeparatorColor,
        };

    private static (GuiTestHarness Harness, MarkdownTableView View) Create(
        IReadOnlyList<IReadOnlyList<RichTextRun>> header,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<RichTextRun>>> rows,
        IReadOnlyList<ColumnAlignment>? alignments = null,
        int width = 800,
        int height = 600,
        ITextMeasurer? measurer = null)
    {
        MarkdownTableView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new MarkdownTableView(ctx.Canvas)
                {
                    Header = header,
                    Rows = rows,
                    Alignments = alignments
                        ?? Enumerable.Repeat(ColumnAlignment.None, header.Count).ToArray(),
                    HeaderRuleColor = RuleColor,
                    RowSeparatorColor = SeparatorColor,
                    CodeChipBackground = ChipBg,
                };
                return view;
            },
            width, height, measurer: measurer);
        return (harness, view);
    }

    private static RecordedText Draw(RecordingCanvas canvas, string text) =>
        canvas.Texts.Single(t => t.Inputs.Text == text);

    private static List<RecordedLine> LinesOf(RecordingCanvas canvas, uint color) =>
        canvas.Lines.Where(l => l.Inputs.Color == color).ToList();

    private sealed class CountingMeasurer : ITextMeasurer
    {
        private readonly SyntheticTextMeasurer _inner = new();
        public int WidthCalls;

        public float MeasureTextWidth(ReadOnlySpan<char> text, TextStyle style)
        {
            WidthCalls++;
            return _inner.MeasureTextWidth(text, style);
        }

        public float MeasureTextPrefix(ReadOnlySpan<char> text, int prefixLength, TextStyle style) =>
            _inner.MeasureTextPrefix(text, prefixLength, style);

        public float MeasureTextLineHeight(TextStyle style) => _inner.MeasureTextLineHeight(style);
    }

    // ---------- design constants ----------

    [Fact]
    public void PaddingAndRuleConstantsArePinned()
    {
        // The geometry below depends on these; a change here is a deliberate redesign.
        Assert.Equal(8f, MarkdownTableView.CellPaddingX);
        Assert.Equal(4f, MarkdownTableView.CellPaddingY);
        Assert.Equal(2f, MarkdownTableView.HeaderRuleThickness);
        Assert.Equal(1f, MarkdownTableView.RowSeparatorThickness);
    }

    // ---------- measurement ----------

    [Fact]
    public void MeasureWidthIsTheMinContentTableWidth()
    {
        // Column mins 32 ("aaaa"/"bbbb" chunks) and 16, plus 16px padding per column: the
        // narrowest layout that never splits an unbreakable chunk. NOT the max-content width —
        // inside HorizontalScrollView a larger intrinsic width would kill wrapping forever.
        var view = StandaloneView(Header("aaaa bbbb", "cc"), Rows());

        Assert.Equal(32f + 16f + 2 * 2 * PadX, view.MeasureWidth());
    }

    [Fact]
    public void MeasureHeightSumsRowHeightsRuleAndSeparators()
    {
        var view = StandaloneView(
            Header("h1", "h2"),
            Rows(new[] { "aaaa bbbb", "cc" }, new[] { "x", "y" }));

        // At 88: columns solve to (40, 16); "aaaa bbbb" wraps to 2 lines, so
        // 24 (header) + 2 (rule) + 40 (wrapped row) + 1 (separator) + 24 = 91.
        Assert.Equal(91f, view.MeasureHeight(88f), 3);

        // At 800 everything sits at max-content on one line: 24 + 2 + 24 + 1 + 24 = 75.
        Assert.Equal(75f, view.MeasureHeight(800f), 3);
    }

    [Fact]
    public void AnEmptyTableMeasuresAndDrawsNothing()
    {
        var view = StandaloneView(
            Header(), Rows(), Array.Empty<ColumnAlignment>());
        Assert.Equal(0f, view.MeasureWidth());
        Assert.Equal(0f, view.MeasureHeight(100f));

        var (h, _) = Create(Header(), Rows(), Array.Empty<ColumnAlignment>());
        using (h)
        {
            var canvas = h.Render();
            Assert.Empty(canvas.Texts);
            Assert.Empty(canvas.Lines);
        }
    }

    // ---------- basic geometry (all-max at 800) ----------
    // header ["ab","cd"], rows [["e","fg"],["hi","j"]]: both columns 16 wide, so column content
    // starts at x=8 and x=40; the table's drawn width is 64.

    private static (GuiTestHarness, MarkdownTableView) CreateBasic() => Create(
        Header("ab", "cd"),
        Rows(new[] { "e", "fg" }, new[] { "hi", "j" }));

    [Fact]
    public void HeaderCellsDrawAtPaddedCellOrigins()
    {
        var (h, _) = CreateBasic();
        using (h)
        {
            var canvas = h.Render();

            var first = Draw(canvas, "ab");
            Assert.Equal(PadX, first.Inputs.Position.Left, 3);
            Assert.Equal(Top - PadY - LineH, first.Inputs.Position.Bottom, 3);

            var second = Draw(canvas, "cd");
            Assert.Equal(16f + 2 * PadX + PadX, second.Inputs.Position.Left, 3); // 40
            Assert.Equal(Top - PadY - LineH, second.Inputs.Position.Bottom, 3);
        }
    }

    [Fact]
    public void HeaderRuleDrawsBelowTheHeaderInItsColorAndThickness()
    {
        var (h, _) = CreateBasic();
        using (h)
        {
            var canvas = h.Render();

            var rule = Assert.Single(LinesOf(canvas, RuleColor));
            Assert.Equal(rule.Inputs.Start.Y, rule.Inputs.End.Y); // horizontal
            Assert.Equal(2f, rule.Inputs.Thickness, 3);
            // Below the header row band (bottom 576), above row 1's content (top 574).
            Assert.InRange(rule.Inputs.Start.Y, 574f, 576f);
            // Spans the table's drawn width (64), not the view's 800.
            Assert.Equal(0f, Math.Min(rule.Inputs.Start.X, rule.Inputs.End.X), 3);
            Assert.Equal(64f, Math.Max(rule.Inputs.Start.X, rule.Inputs.End.X), 3);
        }
    }

    [Fact]
    public void RowSeparatorsDrawBetweenBodyRowsOnly()
    {
        var (h, _) = CreateBasic();
        using (h)
        {
            var canvas = h.Render();

            // Two body rows -> exactly one separator: between them, never after the last row.
            var separator = Assert.Single(LinesOf(canvas, SeparatorColor));
            Assert.Equal(separator.Inputs.Start.Y, separator.Inputs.End.Y);
            Assert.Equal(1f, separator.Inputs.Thickness, 3);
            Assert.InRange(separator.Inputs.Start.Y, 549f, 550f);
            Assert.Equal(64f, Math.Max(separator.Inputs.Start.X, separator.Inputs.End.X), 3);
        }
    }

    [Fact]
    public void BodyRowsStackTopDownBelowTheHeaderRule()
    {
        var (h, _) = CreateBasic();
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(554f, Draw(canvas, "e").Inputs.Position.Bottom, 3);
            Assert.Equal(40f, Draw(canvas, "fg").Inputs.Position.Left, 3);
            Assert.Equal(529f, Draw(canvas, "hi").Inputs.Position.Bottom, 3);
        }
    }

    [Fact]
    public void AHeaderOnlyTableDrawsItsRuleButNoSeparators()
    {
        // The streaming shape: header + delimiter, no rows yet — still reads as a table.
        var (h, view) = Create(Header("ab", "cd"), Rows());
        using (h)
        {
            var canvas = h.Render();

            Assert.Single(LinesOf(canvas, RuleColor));
            Assert.Empty(LinesOf(canvas, SeparatorColor));
            Assert.Equal(26f, view.MeasureHeight(800f), 3); // 24 + 2, nothing below
        }
    }

    // ---------- wrapping (distributed at 88) ----------
    // header ["h1","h2"], row 1 ["aaaa bbbb","cc"], row 2 ["x","y"]: columns solve to (40, 16)
    // (contentAvail 56, mins 32+16, all 8 remainder pixels to column 0), so "aaaa bbbb" wraps.

    [Fact]
    public void CellsWrapWithinTheirColumnAndGrowTheRow()
    {
        var (h, _) = Create(
            Header("h1", "h2"),
            Rows(new[] { "aaaa bbbb", "cc" }, new[] { "x", "y" }),
            width: 88);
        using (h)
        {
            var canvas = h.Render();

            // The wrapped cell: two lines stacked inside row 1, both at the column's left inset.
            var first = Draw(canvas, "aaaa ");
            var second = Draw(canvas, "bbbb");
            Assert.Equal(PadX, first.Inputs.Position.Left, 3);
            Assert.Equal(554f, first.Inputs.Position.Bottom, 3);
            Assert.Equal(PadX, second.Inputs.Position.Left, 3);
            Assert.Equal(554f - LineH, second.Inputs.Position.Bottom, 3);

            // The single-line neighbor is top-aligned in the grown row.
            Assert.Equal(554f, Draw(canvas, "cc").Inputs.Position.Bottom, 3);

            // Row 2 starts below the grown row 1 (40 tall) and its separator:
            // 574 - 40 - 1 - 4 - 16 = 513.
            Assert.Equal(513f, Draw(canvas, "x").Inputs.Position.Bottom, 3);
        }
    }

    // ---------- alignment ----------
    // header ["a","num"], row ["b","7"]: columns 8 and 24 wide at max; column 1's content box is
    // [32, 56).

    [Fact]
    public void RightAlignedColumnDrawsEachLineFlushRight()
    {
        var (h, _) = Create(
            Header("a", "num"),
            Rows(new[] { "b", "7" }),
            new[] { ColumnAlignment.None, ColumnAlignment.Right });
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(PadX, Draw(canvas, "b").Inputs.Position.Left, 3);       // None: flush left
            Assert.Equal(32f, Draw(canvas, "num").Inputs.Position.Left, 3);      // full-width line
            Assert.Equal(48f, Draw(canvas, "7").Inputs.Position.Left, 3);        // 32 + (24 - 8)
        }
    }

    [Fact]
    public void CenterAlignedColumnCentersItsLines()
    {
        var (h, _) = Create(
            Header("a", "num"),
            Rows(new[] { "b", "7" }),
            new[] { ColumnAlignment.None, ColumnAlignment.Center });
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(40f, Draw(canvas, "7").Inputs.Position.Left, 3);        // 32 + (24 - 8)/2
        }
    }

    // ---------- overflow (below min at 40) ----------

    [Fact]
    public void ANarrowWidthLaysOutAtMinWidthsAndOverflows()
    {
        // Mins are 32 + 32 (+32 padding) = 96 > 40: the view keeps min-content columns and
        // simply draws past its width — that overflow is what the widget's HorizontalScrollArea
        // scrolls. Nothing wraps below min-content and no chunk is ever split.
        var (h, view) = Create(
            Header("aaaa", "bbbb"),
            Rows(new[] { "cccc", "dddd" }),
            width: 40);
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(96f, view.MeasureWidth(), 3);
            Assert.Equal(4, canvas.Texts.Count); // each cell stays a single unsplit segment
            Assert.Equal(PadX, Draw(canvas, "aaaa").Inputs.Position.Left, 3);
            Assert.Equal(56f, Draw(canvas, "bbbb").Inputs.Position.Left, 3);     // past width 40
            Assert.Equal(56f, Draw(canvas, "dddd").Inputs.Position.Left, 3);
            Assert.Equal(554f, Draw(canvas, "cccc").Inputs.Position.Bottom, 3);
        }
    }

    // ---------- styles and decorations ----------

    [Fact]
    public void SegmentsDrawInTheirOwnRunStyles()
    {
        // The view is style-agnostic: header cells arrive pre-styled bold from the widget and
        // must draw with exactly the style their run carries.
        var (h, _) = CreateBasic();
        using (h)
        {
            var canvas = h.Render();

            var header = Draw(canvas, "ab");
            Assert.Equal(FontWeight.Bold, header.Inputs.Style.FontWeight.Value);
            Assert.Equal(HeaderColor, header.Inputs.Style.TextColor.Value);

            var body = Draw(canvas, "e");
            Assert.NotEqual(FontWeight.Bold, body.Inputs.Style.FontWeight.Value);
            Assert.Equal(BodyColor, body.Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void CodeChipDrawsBehindCodeCellSegments()
    {
        var codeCell = new[]
        {
            new RichTextRun("x=1", new TextStyle { TextColor = BodyColor }, IsCode: true),
        };
        var rows = new IReadOnlyList<IReadOnlyList<RichTextRun>>[]
        {
            new IReadOnlyList<RichTextRun>[] { BodyCell("a"), codeCell },
        };
        var (h, _) = Create(Header("h1", "h2"), rows);
        using (h)
        {
            var canvas = h.Render();

            var chip = Assert.Single(canvas.Rects, r => r.Inputs.Style.BackgroundColor == ChipBg);
            var text = Draw(canvas, "x=1");
            Assert.True(chip.Inputs.Position.Left <= text.Inputs.Position.Left + 0.001f,
                "chip must start at or before its code segment");
            Assert.True(chip.Inputs.ZIndex < text.Inputs.ZIndex, "chip draws below the code text");
        }
    }

    // ---------- caching ----------

    [Fact]
    public void LayoutCachesAgainstWidthAndContent()
    {
        var counting = new CountingMeasurer();
        var (h, view) = Create(
            Header("ab", "cd"), Rows(new[] { "e", "f" }), measurer: counting);
        using (h)
        {
            h.Render();
            var afterFirst = counting.WidthCalls;
            Assert.True(afterFirst > 0, "the first render must measure");

            // Same width, same cell lists: the cached layout serves measure and draw.
            h.Render();
            Assert.Equal(afterFirst, counting.WidthCalls);

            // New content invalidates: the view re-measures and draws the new cells.
            view.Rows = Rows(new[] { "zz", "qq" });
            var canvas = h.Render();
            Assert.True(counting.WidthCalls > afterFirst, "changed rows must re-measure");
            Assert.Contains(canvas.Texts, t => t.Inputs.Text == "zz");
        }
    }
}
