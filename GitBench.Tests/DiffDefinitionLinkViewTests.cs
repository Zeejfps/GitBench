using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// The link a held Ctrl/Cmd draws over a symbol, on the real DiffContentView. The synthetic
// measurer pins geometry (16px rows, 8px mono advance), so the wash and the rule can be checked
// against the columns the mark actually named — including on a line whose leading tab moves every
// glyph on it.
public class DiffDefinitionLinkViewTests
{
    private const float RowH = 16f;
    private const float Advance = 8f;
    private const float Top = 600f;

    // One hunk ten lines in. Rows: [0] top bar, [1] context, [2] context (tab-indented),
    // [3] context, [4] EOF bar. Line numbers reach 12, so both gutters are two digits wide.
    private static DiffResult Diff()
    {
        var hunk = new DiffHunk(10, 3, 10, 3, null, new[]
        {
            new DiffLine(DiffLineKind.Context, 10, 10, "var alpha = 1;"),
            new DiffLine(DiffLineKind.Context, 11, 11, "\tCompute(alpha);"),
            new DiffLine(DiffLineKind.Context, 12, 12, "return alpha;"),
        });
        return new DiffResult(
            RepoId: Guid.Empty,
            Path: "file.cs",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: new[] { hunk },
            Truncated: false,
            ErrorMessage: null);
    }

    private static (GuiTestHarness Harness, DiffContentView View) Create()
    {
        DiffContentView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new DiffContentView(ctx);
                return view;
            },
            width: 800,
            height: 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            });
        view.SetRenderState(new DiffRenderState.Loaded(Diff()));
        harness.Render(); // resolve font metrics
        return (harness, view);
    }

    private static float TextOrigin()
    {
        var gutter = 2 * Advance + 8f; // two-digit line numbers
        return DiffRowPainter.LineTextOriginX(0f, gutter, singleGutter: false);
    }

    private static float XOfColumn(int column) => TextOrigin() + column * Advance;
    private static float RowCenterY(int row) => Top - row * RowH - RowH / 2f;

    private static FileSpan Span(int line, int start, int end) =>
        new(new FileLine(line), new RawColumn(start), new RawColumn(end));

    private static RectF TintRect(RecordingCanvas canvas)
    {
        var color = ThemeStyles.Dark.DiffContent.LinkBackground;
        var rects = canvas.Rects
            .Where(r => r.Inputs.Style.BackgroundColor == color)
            .Select(r => r.Inputs.Position)
            .ToList();
        return Assert.Single(rects);
    }

    private static RecordedLine UnderlineOn(RecordingCanvas canvas)
    {
        var color = ThemeStyles.Dark.DiffContent.LinkUnderline;
        return Assert.Single(canvas.Lines.Where(l => l.Inputs.Color == color).ToList());
    }

    private static bool AnyLinkDrawn(RecordingCanvas canvas)
    {
        var styles = ThemeStyles.Dark.DiffContent;
        return canvas.Rects.Any(r => r.Inputs.Style.BackgroundColor == styles.LinkBackground) ||
            canvas.Lines.Any(l => l.Inputs.Color == styles.LinkUnderline);
    }

    [Fact]
    public void AMarkedSymbolIsWashedAndUnderlinedAcrossItsOwnColumns()
    {
        var (h, view) = Create();
        using (h)
        {
            view.ShowDefinitionLink(Span(line: 10, start: 4, end: 9)); // "alpha"

            var canvas = h.Render();

            var tint = TintRect(canvas);
            Assert.Equal(XOfColumn(4), tint.Left, 3);
            Assert.Equal(5 * Advance, tint.Width, 3);

            var rule = UnderlineOn(canvas);
            Assert.Equal(XOfColumn(4), rule.Inputs.Start.X, 3);
            Assert.Equal(XOfColumn(9), rule.Inputs.End.X, 3);
            Assert.Equal(rule.Inputs.Start.Y, rule.Inputs.End.Y, 3);
        }
    }

    // The mark is in raw columns and the painter draws in expanded ones, so a leading tab has to
    // widen the offset. Without the conversion the link would sit tab-width short of its word.
    [Fact]
    public void AMarkOnATabbedLineLandsOnTheGlyphsNotBesideThem()
    {
        var (h, view) = Create();
        using (h)
        {
            view.ShowDefinitionLink(Span(line: 11, start: 1, end: 8)); // "Compute" after a tab

            var tint = TintRect(h.Render());

            Assert.Equal(XOfColumn(DiffOptions.TabWidth), tint.Left, 3);
            Assert.Equal(7 * Advance, tint.Width, 3);
        }
    }

    [Fact]
    public void ClearingTheMarkTakesTheLinkAwayAgain()
    {
        var (h, view) = Create();
        using (h)
        {
            view.ShowDefinitionLink(Span(line: 10, start: 4, end: 9));
            Assert.True(AnyLinkDrawn(h.Render()));

            view.ShowDefinitionLink(null);

            Assert.False(AnyLinkDrawn(h.Render()));
        }
    }

    [Fact]
    public void AMarkOnALineThatIsNotOnScreenDrawsNothing()
    {
        var (h, view) = Create();
        using (h)
        {
            view.ShowDefinitionLink(Span(line: 400, start: 0, end: 4));

            Assert.False(AnyLinkDrawn(h.Render()));
        }
    }

    [Fact]
    public void ThePointerOverASymbolNamesTheWholeSymbol()
    {
        var (h, view) = Create();
        using (h)
        {
            var at = view.HitTestIdentifier(new PointF(XOfColumn(6), RowCenterY(1)));

            Assert.Equal(Span(line: 10, start: 4, end: 9), at);
        }
    }

    [Theory]
    [InlineData(3)]   // the space before "alpha"
    [InlineData(10)]  // the "=" operator
    [InlineData(40)]  // out past the end of the line
    [InlineData(-4)]  // back over the line-number gutters
    public void ThePointerOffASymbolNamesNothing(int column)
    {
        var (h, view) = Create();
        using (h)
        {
            Assert.Null(view.HitTestIdentifier(new PointF(XOfColumn(column), RowCenterY(1))));
        }
    }
}
