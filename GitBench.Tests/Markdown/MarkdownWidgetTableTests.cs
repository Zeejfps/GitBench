using GitBench.Features.Diff;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Step 6 integration: a document with a TableBlock now RENDERS the table through MarkdownWidget
// (flipping Step 5's skipped-block placeholder — whose own pin, "other blocks render and never
// throw", stays true). Real parser, real ThemeService, synthetic metrics (8px/char, 16px lines).
//
// Pinned contracts (binding on the implementer):
// - Header cells build through InlineRunBuilder with bold: true — FontWeight.Bold at
//   FontSize.Body (13) in Palette.TextStrong; body cells plain at Body 13 in Palette.TextBody.
// - Per-column alignment from the delimiter row reaches the drawn geometry (a right-aligned
//   column's cells flush right against the column edge the header occupies).
// - The header rule and row separators draw in MarkdownStyles.TableHeaderRule /
//   TableRowSeparator from the ACTIVE palette; the header rule is the thicker of the two. The
//   two slots must be non-zero and distinct in both palettes (the identical placeholders in
//   ThemeStyles.Markdown.cs keep that red until the implementer picks real values).
// - The table nests in a HorizontalScrollArea (the code-block precedent): fill-and-wrap when
//   min-content fits the viewport, horizontal scrolling when it does not.
// - Cell inline styling flows through InlineRunBuilder like paragraphs: code cells get the mono
//   family and chip, italic cells the italic family.
public class MarkdownWidgetTableTests
{
    private static GuiTestHarness Create(
        string markdown, int width = 800, int height = 600, ThemeMode mode = ThemeMode.Dark) =>
        GuiTestHarness.Create(
            ctx => new MarkdownWidget
            {
                Document = new BasicMarkdownParser().Parse(markdown),
            }.BuildView(ctx),
            width, height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(mode)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
            });

    private static ThemeStyles Dark => ThemeStyles.Dark;

    private static RecordedText Draw(RecordingCanvas canvas, string text) =>
        canvas.Texts.Single(t => t.Inputs.Text == text);

    private const string SimpleTable = "| a | b |\n|---|---:|\n| 1 | 2 |";

    // ---------- the table renders ----------

    [Fact]
    public void ADocumentWithATableRendersItsHeaderAndBodyCells()
    {
        using var h = Create(SimpleTable);
        var canvas = h.Render();

        Assert.NotNull(Draw(canvas, "a"));
        Assert.NotNull(Draw(canvas, "b"));
        Assert.NotNull(Draw(canvas, "1"));
        Assert.NotNull(Draw(canvas, "2"));
    }

    [Fact]
    public void SurroundingBlocksStillRenderAroundTheTable()
    {
        // The Step 5 pin ("a document containing a table renders its other blocks and never
        // throws") must survive the table becoming real.
        using var h = Create("# Title\n\n" + SimpleTable + "\n\nafter");
        var canvas = h.Render();

        Assert.NotNull(Draw(canvas, "Title"));
        Assert.NotNull(Draw(canvas, "after"));
    }

    // ---------- cell styling ----------

    [Fact]
    public void HeaderCellsAreBoldInStrongTextAndBodyCellsPlainInBodyText()
    {
        using var h = Create(SimpleTable);
        var canvas = h.Render();

        var header = Draw(canvas, "a");
        Assert.Equal(FontWeight.Bold, header.Inputs.Style.FontWeight.Value);
        Assert.Equal(13f, header.Inputs.Style.FontSize.Value);
        Assert.Equal(Dark.Palette.TextStrong, header.Inputs.Style.TextColor.Value);

        var body = Draw(canvas, "1");
        Assert.NotEqual(FontWeight.Bold, body.Inputs.Style.FontWeight.Value);
        Assert.Equal(13f, body.Inputs.Style.FontSize.Value);
        Assert.Equal(Dark.Palette.TextBody, body.Inputs.Style.TextColor.Value);
    }

    [Fact]
    public void CellInlineStylingFlowsThroughTheRunBuilder()
    {
        using var h = Create("| `x` | *it* |\n|---|---|\n| a | b |");
        var canvas = h.Render();

        var code = Draw(canvas, "x");
        Assert.Equal(DiffOptions.MonoFontFamily, code.Inputs.Style.FontFamily.Value);
        Assert.Equal(Dark.Markdown.CodeChipText, code.Inputs.Style.TextColor.Value);
        Assert.Contains(canvas.Rects,
            r => r.Inputs.Style.BackgroundColor == Dark.Markdown.CodeChipBackground);

        Assert.Equal(MarkdownFonts.ItalicFamily, Draw(canvas, "it").Inputs.Style.FontFamily.Value);
    }

    // ---------- alignment ----------

    [Fact]
    public void RightAlignedColumnFlushesItsCellsRight()
    {
        // Column b is right-aligned (":---" vs "---:"): "2" (1 char) must share its right edge
        // with the widest cell "bbb" (3 chars), while the left-aligned column keeps shared left
        // edges.
        using var h = Create("| a | bbb |\n|---|---:|\n| 1 | 2 |");
        var canvas = h.Render();

        Assert.Equal(
            Draw(canvas, "bbb").Inputs.Position.Right,
            Draw(canvas, "2").Inputs.Position.Right, 3);
        Assert.Equal(
            Draw(canvas, "a").Inputs.Position.Left,
            Draw(canvas, "1").Inputs.Position.Left, 3);
    }

    // ---------- theme ----------

    [Fact]
    public void HeaderRuleAndRowSeparatorsDrawInTheThemeSlots()
    {
        using var h = Create("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |");
        var canvas = h.Render();

        var rules = canvas.Lines
            .Where(l => l.Inputs.Color == Dark.Markdown.TableHeaderRule).ToList();
        var separators = canvas.Lines
            .Where(l => l.Inputs.Color == Dark.Markdown.TableRowSeparator).ToList();
        Assert.NotEmpty(rules);
        Assert.NotEmpty(separators);
        Assert.True(rules[0].Inputs.Thickness > separators[0].Inputs.Thickness,
            "the header rule must be heavier than a row separator");
    }

    [Fact]
    public void TableThemeSlotsAreNonZeroAndDistinctInBothPalettes()
    {
        // Rejects the Step 6 placeholders (both slots = p.Border): the header rule must read
        // stronger than the row separator, so the implementer picks two distinct colors per
        // palette.
        foreach (var styles in new[] { ThemeStyles.Dark, ThemeStyles.Light })
        {
            Assert.NotEqual(0u, styles.Markdown.TableHeaderRule);
            Assert.NotEqual(0u, styles.Markdown.TableRowSeparator);
            Assert.NotEqual(styles.Markdown.TableHeaderRule, styles.Markdown.TableRowSeparator);
        }
    }

    [Fact]
    public void TableBuiltUnderTheLightThemeUsesTheLightSlots()
    {
        using var h = Create(SimpleTable, mode: ThemeMode.Light);
        var canvas = h.Render();

        Assert.Contains(canvas.Lines,
            l => l.Inputs.Color == ThemeStyles.Light.Markdown.TableHeaderRule);
    }

    // ---------- structure ----------

    [Fact]
    public void TableContentLivesInAHorizontalScrollArea()
    {
        // Structure pin (the code-block precedent): the table always nests in a horizontal
        // scroll viewport. Because MarkdownTableView's intrinsic width is its min-content total,
        // the viewport lays it out at the full width when min-content fits (cells wrap), and at
        // min widths with sideways scrolling only when even those overflow. The document here is
        // table-only, so the scroll view can only be the table's.
        using var h = Create(SimpleTable);
        h.Render();

        Assert.Contains(h.Root.SelfAndDescendants(), v => v is HorizontalScrollView);
    }

    // ---------- degenerate shapes ----------

    [Fact]
    public void AHeaderOnlyTableRendersItsHeaderRow()
    {
        // The streaming shape: header + delimiter parsed, no data rows yet.
        using var h = Create("| a | b |\n|---|---|");
        var canvas = h.Render();

        Assert.NotNull(Draw(canvas, "a"));
        Assert.NotNull(Draw(canvas, "b"));
    }

    [Fact]
    public void ARaggedTableRendersItsPaddedCellsWithoutThrowing()
    {
        using var h = Create("| a | b |\n|---|---|\n| 1 |");
        var canvas = h.Render();

        Assert.NotNull(Draw(canvas, "1"));
    }
}
