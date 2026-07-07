using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// Hunk-gap expanders in the diff body: separator bars carry the expander icons and hidden-line
// labels, expanded gap lines render as plain context rows outside every hunk range, and a fully
// expanded gap drops its bar. Drives the real DiffContentView headlessly; the synthetic text
// measurer makes geometry deterministic (16px rows, 8px mono advance).
public class DiffContextExpansionViewTests
{
    private const float RowH = 16f;
    private const float Top = 600f;

    // Two hunks with a 9-line gap above hunk 0, a 47-line gap between them, and full trailing
    // context on hunk 1 (so the EOF heuristic keeps the trailing bar).
    private static DiffResult TwoHunkDiff()
    {
        var h0 = new DiffHunk(10, 3, 10, 5, null, new[]
        {
            new DiffLine(DiffLineKind.Context, 10, 10, "alpha"),
            new DiffLine(DiffLineKind.Added, null, 11, "beta"),
            new DiffLine(DiffLineKind.Added, null, 12, "gamma"),
            new DiffLine(DiffLineKind.Context, 11, 13, "delta"),
            new DiffLine(DiffLineKind.Context, 12, 14, "epsilon"),
        });
        var h1 = new DiffHunk(60, 4, 62, 4, null, new[]
        {
            new DiffLine(DiffLineKind.Removed, 60, null, "old-x"),
            new DiffLine(DiffLineKind.Added, null, 62, "new-x"),
            new DiffLine(DiffLineKind.Context, 61, 63, "one"),
            new DiffLine(DiffLineKind.Context, 62, 64, "two"),
            new DiffLine(DiffLineKind.Context, 63, 65, "three"),
        });
        return new DiffResult(
            RepoId: Guid.Empty,
            Path: "file.txt",
            OldPath: null,
            Side: DiffSide.Unstaged,
            IsBinary: false,
            IsModeOnly: false,
            OldMode: null,
            NewMode: null,
            Hunks: new[] { h0, h1 },
            Truncated: false,
            ErrorMessage: null);
    }

    private static ContextExpansion Expansion(params (int Gap, int Top, int Bottom)[] gaps)
    {
        var lines = new List<string>();
        for (var n = 1; n <= 80; n++) lines.Add($"line {n}");
        var shown = new Dictionary<int, GapShown>();
        foreach (var g in gaps) shown[g.Gap] = new GapShown(g.Top, g.Bottom);
        return new ContextExpansion(lines, Truncated: false, shown);
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
        return (harness, view);
    }

    private static bool HasText(RecordingCanvas canvas, string text)
    {
        foreach (var t in canvas.Texts)
            if (t.Inputs.Text == text) return true;
        return false;
    }

    private static int CountText(RecordingCanvas canvas, string text)
    {
        var count = 0;
        foreach (var t in canvas.Texts)
            if (t.Inputs.Text == text) count++;
        return count;
    }

    // Every separator bar draws exactly one full-row rect in the section background color
    // (banners would too, but these diffs have none), so this counts the bars on screen.
    private static int CountBars(RecordingCanvas canvas)
    {
        var section = ThemeStyles.Dark.DiffContent.SectionBackground;
        var count = 0;
        foreach (var r in canvas.Rects)
            if (r.Inputs.Style.BackgroundColor == section) count++;
        return count;
    }

    private static float RowCenterY(int rowIndex) => Top - rowIndex * RowH - RowH / 2f;

    [Fact]
    public void BarsShowExpanderIconsAndHiddenCounts()
    {
        var (h, view) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff()));
            var canvas = h.Render();

            Assert.True(HasText(canvas, "@@ -10,3 +10,5 @@"));
            Assert.True(HasText(canvas, "@@ -60,4 +62,4 @@"));
            // Gap 0 hides lines 1..9; the middle gap hides 15..61.
            Assert.True(HasText(canvas, "… 9 hidden lines"));
            Assert.True(HasText(canvas, "… 47 hidden lines"));
            // Four bars: top-of-file, the split gap's two, and the EOF bar. The tear between
            // the split gap's bars draws on the plain background (no section fill).
            Assert.Equal(4, CountBars(canvas));
            // The EOF bar has no exact count yet, so only the two labels above are drawn.
            var labels = 0;
            foreach (var t in canvas.Texts)
                if (t.Inputs.Text.Contains("hidden lines")) labels++;
            Assert.Equal(2, labels);
            // The tear's zigzag and the expander arrows are line-drawn.
            Assert.NotEmpty(canvas.Lines);
        }
    }

    [Fact]
    public void EofBarIsOmittedWhenTheLastHunkReachesEof()
    {
        var diff = TwoHunkDiff();
        // Trim hunk 1's trailing context below ContextLines: git ran out of file.
        var h1 = diff.Hunks[1];
        var lines = new List<DiffLine>(h1.Lines);
        lines.RemoveAt(lines.Count - 1);
        diff = diff with { Hunks = new[] { diff.Hunks[0], h1 with { Lines = lines } } };

        var (h, view) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(diff));
            var canvas = h.Render();

            // Three bars only — the trailing EOF bar is gone.
            Assert.Equal(3, CountBars(canvas));
        }
    }

    [Fact]
    public void ClickingExpanderIconsReportsGapAndDirection()
    {
        var (h, view) = Create();
        using (h)
        {
            var calls = new List<(int Gap, GapExpandDirection Dir)>();
            view.OnExpandGap = (gap, dir) => calls.Add((gap, dir));
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff()));
            h.Render(); // resolve font metrics so hit-testing has row geometry

            // Rows: [0] bar 0, [1..5] hunk 0, [6] split gap top bar, [7] tear, [8] split gap
            // bottom bar, [9..13] hunk 1, [14] EOF bar.
            h.Click(15f, RowCenterY(0));   // top-of-file bar: lone up arrow
            h.Click(15f, RowCenterY(6));   // split gap, top bar: down arrow
            h.Click(15f, RowCenterY(7));   // split gap, tear: unfold-all
            h.Click(15f, RowCenterY(8));   // split gap, bottom bar: up arrow
            h.Click(15f, RowCenterY(14));  // EOF bar: lone down arrow

            Assert.Equal(new[]
            {
                (0, GapExpandDirection.Up),
                (1, GapExpandDirection.Down),
                (1, GapExpandDirection.All),
                (1, GapExpandDirection.Up),
                (2, GapExpandDirection.Down),
            }, calls);
        }
    }

    [Fact]
    public void ClickingAnywhereOnABarExpandsIt()
    {
        var (h, view) = Create();
        using (h)
        {
            var calls = new List<(int Gap, GapExpandDirection Dir)>();
            view.OnExpandGap = (gap, dir) => calls.Add((gap, dir));
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff()));
            h.Render();

            // Same bars as ClickingExpanderIconsReportsGapAndDirection, but clicked far to the
            // right of the arrow — the whole bar is the hit target, not just the icon cell.
            h.Click(400f, RowCenterY(0));   // top-of-file bar: up
            h.Click(400f, RowCenterY(6));   // split gap, top bar: down
            h.Click(400f, RowCenterY(7));   // split gap, tear: unfold-all
            h.Click(400f, RowCenterY(8));   // split gap, bottom bar: up
            h.Click(400f, RowCenterY(14));  // EOF bar: down

            Assert.Equal(new[]
            {
                (0, GapExpandDirection.Up),
                (1, GapExpandDirection.Down),
                (1, GapExpandDirection.All),
                (1, GapExpandDirection.Up),
                (2, GapExpandDirection.Down),
            }, calls);
        }
    }

    [Fact]
    public void ExpandedRowsRenderBetweenBarAndHunkWithBothGutterNumbers()
    {
        var (h, view) = Create();
        using (h)
        {
            // Gap 0 expanded upward by 4: lines 6..9 revealed below the bar, 5 still hidden.
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff(), Expansion: Expansion((0, 0, 4))));
            var canvas = h.Render();

            Assert.True(HasText(canvas, "line 6"));
            Assert.True(HasText(canvas, "line 9"));
            Assert.True(HasText(canvas, "… 5 hidden lines"));
            // Unchanged lines above hunk 0: old == new, so the number draws in both gutters.
            Assert.Equal(2, CountText(canvas, "6"));

            // Y-up: the revealed line sits below the bar and above the hunk's first line.
            float YOf(string text)
            {
                foreach (var t in canvas.Texts)
                    if (t.Inputs.Text == text) return t.Inputs.Position.Bottom;
                throw new InvalidOperationException($"'{text}' not drawn");
            }
            Assert.True(YOf("line 6") < YOf("@@ -10,3 +10,5 @@"));
            Assert.True(YOf("line 6") > YOf("alpha"));
        }
    }

    [Fact]
    public void FullyExpandedGapDropsItsBarAndEofCountBecomesExact()
    {
        var (h, view) = Create();
        using (h)
        {
            // The whole 47-line middle gap revealed; the 80-line file leaves 15 below hunk 1.
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff(), Expansion: Expansion((1, 47, 0))));

            var canvas = h.Render();
            Assert.True(HasText(canvas, "line 15")); // reads on from hunk 0 with no bar between
            Assert.False(HasText(canvas, "@@ -60,4 +62,4 @@"));

            // The dropped bar stays dropped at the bottom too, and the EOF label is exact now.
            view.SetVerticalNormalizedScrollPosition(1f);
            canvas = h.Render();
            Assert.True(HasText(canvas, "new-x"));
            Assert.False(HasText(canvas, "@@ -60,4 +62,4 @@"));
            Assert.True(HasText(canvas, "… 15 hidden lines"));
        }
    }

    [Fact]
    public void ExpandedRowsAreOutsideTheHunkForHoverButtons()
    {
        var (h, view) = Create();
        using (h)
        {
            view.SetRenderState(new DiffRenderState.Loaded(TwoHunkDiff(), Expansion: Expansion((0, 0, 4))));
            h.Render();

            // Rows: [0] bar 0, [1..4] expanded 6..9, [5..9] hunk 0 lines. Hovering an expanded
            // row shows no hunk buttons (its _rowToHunk is -1)…
            h.MoveTo(400f, RowCenterY(2));
            var canvas = h.Render();
            Assert.False(HasText(canvas, "Stage"));

            // …while hovering a real hunk line shows Stage/Discard for the unstaged side.
            h.MoveTo(400f, RowCenterY(6));
            canvas = h.Render();
            Assert.True(HasText(canvas, "Stage"));
            Assert.True(HasText(canvas, "Discard"));
        }
    }
}
