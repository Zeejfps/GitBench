using GitBench.Features.Markdown.Rendering;
using Xunit;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Testing;

namespace GitBench.Tests.Markdown;

// Harness-driven tests for RichTextView: it must measure exactly like its RichTextLayout,
// emit one DrawText per segment in that segment's run style, decorate code segments with a
// chip rect (below the text's z) and underlined segments with a DrawLine, and answer LinkAt
// in the same coordinate space mouse events arrive in. Synthetic metrics pin the geometry:
// 8px per UTF-16 unit, 16px line height; the harness root is the view itself, so with a
// 600px-tall viewport the first line's rect is [.., 584 16] and lines stack downward.
//
// Style assertions double as an aliasing pin: RecordingCanvas captures the TextStyle by
// reference, so per-segment assertions on TextColor only hold if the view draws each run with
// a style whose values are stable per call (the run's own instance or a copy) — never one
// shared instance mutated between DrawText calls.
public class RichTextViewTests
{
    private const float W = 8f;
    private const float LineH = 16f;
    private const float Top = 600f;

    private const uint PlainColor = 0xFF111111;
    private const uint SecondColor = 0xFF222222;
    private const uint LinkColor = 0xFF3366FF;
    private const uint ChipBg = 0xFF2A2A3A;
    private const uint HoverColor = 0xFFFF8800;
    private const string Url = "https://example.com/docs";

    private static TextStyle Style(uint color) => new() { TextColor = color };

    private static RichTextRun Run(string text, uint color = PlainColor) => new(text, Style(color));

    private static RichTextRun Code(string text) => new(text, Style(SecondColor), IsCode: true);

    private static RichTextRun Link(string text, string url = Url) =>
        new(text, Style(LinkColor), Underline: true, LinkUrl: url);

    private static (GuiTestHarness Harness, RichTextView View) Create(
        IReadOnlyList<RichTextRun> runs, int width = 800, int height = 600)
    {
        RichTextView view = null!;
        var harness = GuiTestHarness.Create(
            ctx =>
            {
                view = new RichTextView(ctx.Canvas)
                {
                    Runs = runs,
                    CodeChipBackground = ChipBg,
                    LinkHoverColor = HoverColor,
                };
                return view;
            },
            width, height);
        return (harness, view);
    }

    private static float LineBottom(int line) => Top - (line + 1) * LineH;

    // ---------- measurement (mirrors the layout) ----------

    [Fact]
    public void MeasuresNaturalWidthOfTheWidestLine()
    {
        var view = new RichTextView(new RecordingCanvas()) { Runs = new[] { Run("aa\nbbbb") } };

        Assert.Equal(4 * W, view.MeasureWidth());
    }

    [Fact]
    public void MeasuresHeightForWrappedContentAtAGivenWidth()
    {
        var view = new RichTextView(new RecordingCanvas()) { Runs = new[] { Run("aa bb cc") } };

        Assert.Equal(2 * LineH, view.MeasureHeight(40f));
    }

    [Fact]
    public void EmptyRunsMeasureZero()
    {
        var view = new RichTextView(new RecordingCanvas());

        Assert.Equal(0f, view.MeasureWidth());
        Assert.Equal(0f, view.MeasureHeight(100f));
    }

    [Fact]
    public void ChangingRunsInvalidatesTheCachedLayout()
    {
        var view = new RichTextView(new RecordingCanvas()) { Runs = new[] { Run("aa bb cc") } };
        Assert.Equal(2 * LineH, view.MeasureHeight(40f));

        view.Runs = new[] { Run("aa") };
        Assert.Equal(LineH, view.MeasureHeight(40f));
    }

    [Fact]
    public void ChangingWidthInvalidatesTheCachedLayout()
    {
        var view = new RichTextView(new RecordingCanvas()) { Runs = new[] { Run("aa bb cc") } };

        Assert.Equal(2 * LineH, view.MeasureHeight(40f));
        Assert.Equal(LineH, view.MeasureHeight(800f));
    }

    // ---------- drawing ----------

    [Fact]
    public void DrawsOneDrawTextPerSegmentAtSegmentGeometry()
    {
        var (h, _) = Create(new[] { Run("hello "), Run("world", SecondColor) });
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(2, canvas.Texts.Count);
            var first = canvas.Texts.Single(t => t.Inputs.Text == "hello ");
            Assert.Equal(0f, first.Inputs.Position.Left, 3);
            Assert.Equal(LineBottom(0), first.Inputs.Position.Bottom, 3);
            Assert.Equal(6 * W, first.Inputs.Position.Width, 3);
            var second = canvas.Texts.Single(t => t.Inputs.Text == "world");
            Assert.Equal(6 * W, second.Inputs.Position.Left, 3);
            Assert.Equal(LineBottom(0), second.Inputs.Position.Bottom, 3);
            Assert.Equal(5 * W, second.Inputs.Position.Width, 3);
        }
    }

    [Fact]
    public void DrawsEachSegmentWithItsOwnRunStyle()
    {
        var (h, _) = Create(new[] { Run("hello "), Run("world", SecondColor) });
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(PlainColor, canvas.Texts.Single(t => t.Inputs.Text == "hello ").Inputs.Style.TextColor.Value);
            Assert.Equal(SecondColor, canvas.Texts.Single(t => t.Inputs.Text == "world").Inputs.Style.TextColor.Value);
        }
    }

    [Fact]
    public void WrappedLinesDrawTopDown()
    {
        var (h, _) = Create(new[] { Run("aa bb cc") }, width: 40);
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(2, canvas.Texts.Count);
            var first = canvas.Texts.Single(t => t.Inputs.Text == "aa bb ");
            Assert.Equal(LineBottom(0), first.Inputs.Position.Bottom, 3);
            var second = canvas.Texts.Single(t => t.Inputs.Text == "cc");
            Assert.Equal(LineBottom(1), second.Inputs.Position.Bottom, 3);
        }
    }

    [Fact]
    public void CodeChipIsDrawnBehindCodeSegments()
    {
        // "a " then code "x=1": the chip rect carries the configured background, spans the code
        // segment's x-range on its line band, and sits below the segment's text in z.
        var (h, _) = Create(new[] { Run("a "), Code("x=1") });
        using (h)
        {
            var canvas = h.Render();

            var chip = Assert.Single(canvas.Rects, r => r.Inputs.Style.BackgroundColor == ChipBg);
            Assert.True(chip.Inputs.Position.Left <= 2 * W + 0.001f, "chip must start at or before the code segment");
            Assert.True(chip.Inputs.Position.Right >= 5 * W - 0.001f, "chip must extend to or past the code segment");
            Assert.True(chip.Inputs.Position.Bottom >= LineBottom(0) - 0.001f, "chip stays inside its line band");
            Assert.True(chip.Inputs.Position.Top <= Top + 0.001f, "chip stays inside its line band");

            var text = canvas.Texts.Single(t => t.Inputs.Text == "x=1");
            Assert.True(chip.Inputs.ZIndex < text.Inputs.ZIndex, "chip draws below the code text");
        }
    }

    [Fact]
    public void NoChipIsDrawnForPlainRuns()
    {
        var (h, _) = Create(new[] { Run("plain "), Run("text", SecondColor) });
        using (h)
        {
            var canvas = h.Render();

            Assert.DoesNotContain(canvas.Rects, r => r.Inputs.Style.BackgroundColor == ChipBg);
        }
    }

    [Fact]
    public void WrappedCodeRunGetsAChipPerLineSegment()
    {
        var (h, _) = Create(new[] { Code("aaaa bbbb") }, width: 40);
        using (h)
        {
            var canvas = h.Render();

            var chips = canvas.Rects.Where(r => r.Inputs.Style.BackgroundColor == ChipBg).ToList();
            Assert.Equal(2, chips.Count);
        }
    }

    [Fact]
    public void UnderlineSpansTheLinkSegmentInItsTextColor()
    {
        var (h, _) = Create(new[] { Run("go "), Link("here") });
        using (h)
        {
            var canvas = h.Render();

            var underline = Assert.Single(canvas.Lines);
            Assert.Equal(underline.Inputs.Start.Y, underline.Inputs.End.Y);
            Assert.Equal(3 * W, Math.Min(underline.Inputs.Start.X, underline.Inputs.End.X), 3);
            Assert.Equal(7 * W, Math.Max(underline.Inputs.Start.X, underline.Inputs.End.X), 3);
            Assert.InRange(underline.Inputs.Start.Y, LineBottom(0), Top);
            Assert.Equal(LinkColor, underline.Inputs.Color);
        }
    }

    [Fact]
    public void WrappedLinkGetsAnUnderlinePerLineSegment()
    {
        var (h, _) = Create(new[] { Link("aaaa bbbb") }, width: 40);
        using (h)
        {
            var canvas = h.Render();

            Assert.Equal(2, canvas.Lines.Count);
            var second = canvas.Lines.Single(l => l.Inputs.Start.Y < LineBottom(0));
            Assert.InRange(second.Inputs.Start.Y, LineBottom(1), LineBottom(0));
        }
    }

    // ---------- link hit-testing ----------

    [Fact]
    public void LinkAtReturnsTheUrlInsideALinkSegmentAndNullOutside()
    {
        // "click " spans x [0,48), "here" spans [48,80) on the first line band [584,600).
        var (h, view) = Create(new[] { Run("click "), Link("here") });
        using (h)
        {
            h.Render();

            Assert.Equal(Url, view.LinkAt(new PointF(60f, 592f)));
            Assert.Null(view.LinkAt(new PointF(20f, 592f)));      // plain segment
            Assert.Null(view.LinkAt(new PointF(120f, 592f)));     // past the end of the line
            Assert.Null(view.LinkAt(new PointF(60f, 560f)));      // below the only line
        }
    }

    [Fact]
    public void LinkAtFindsAWrappedLinkOnItsSecondLine()
    {
        // At 48px "aaaa bbbb" wraps to "aaaa " / "bbbb"; the second-line segment is still the
        // same link, and x past the first line's last glyph is not.
        var (h, view) = Create(new[] { Link("aaaa bbbb") }, width: 48);
        using (h)
        {
            h.Render();

            Assert.Equal(Url, view.LinkAt(new PointF(10f, 576f)));
            Assert.Null(view.LinkAt(new PointF(44f, 592f)));      // past "aaaa " on line 1
        }
    }

    // ---------- hover recolor ----------

    [Fact]
    public void HoveredLinkDrawsInTheHoverColor()
    {
        var (h, view) = Create(new[] { Run("go "), Link("here") });
        using (h)
        {
            view.SetHoveredLink(Url);
            var canvas = h.Render();
            Assert.Equal(HoverColor, canvas.Texts.Single(t => t.Inputs.Text == "here").Inputs.Style.TextColor.Value);
            Assert.Equal(PlainColor, canvas.Texts.Single(t => t.Inputs.Text == "go ").Inputs.Style.TextColor.Value);

            view.SetHoveredLink(null);
            canvas = h.Render();
            Assert.Equal(LinkColor, canvas.Texts.Single(t => t.Inputs.Text == "here").Inputs.Style.TextColor.Value);
        }
    }
}
